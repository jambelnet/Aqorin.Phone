using System.Runtime.InteropServices;
using AVFoundation;
using Aqorin.Phone.Core.Abstractions;
using UIKit;

namespace Aqorin.Phone.Mobile.Platforms.iOS;

/// <summary>iOS full-duplex voice audio backed by AVAudioEngine and AVAudioSession.</summary>
public sealed class IosAudioDeviceService : IAudioDeviceService, ICallAudioRouteController
{
    private readonly IosAudioEngineHost _host = new();
    private bool _disposed;

    public AudioBackendInfo Backend { get; } = new("iOS AVAudioEngine", UIDevice.CurrentDevice.SystemVersion, true, null);

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() =>
    [
        new AudioDeviceInfo("default", "iPhone microphone", true, 1, 0, 48000),
    ];

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() =>
    [
        new AudioDeviceInfo("default", "Call audio route", true, 0, 1, 48000),
    ];

    public IAudioCaptureStream OpenCapture(AudioStreamRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new IosAudioCaptureStream(_host, request);
    }

    public IAudioPlaybackStream OpenPlayback(AudioStreamRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new IosAudioPlaybackStream(_host, request);
    }

    public Task SetSpeakerEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _host.SetSpeakerEnabled(enabled);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.Dispose();
    }
}

internal sealed class IosAudioEngineHost : IDisposable
{
    private readonly object _gate = new();
    private readonly AVAudioSession _session = AVAudioSession.SharedInstance();
    private readonly AVAudioEngine _engine = new();
    private readonly AVAudioPlayerNode _player = new();
    private AVAudioFormat? _callFormat;
    private bool _playerAttached;
    private bool _captureStarted;
    private bool _playbackStarted;
    private bool _disposed;

    public IosAudioEngineHost()
    {
        if (OperatingSystem.IsIOSVersionAtLeast(17))
        {
            var audioApplication = AVAudioApplication.SharedInstance;
            if (audioApplication.RecordPermission == AVAudioApplicationRecordPermission.Undetermined)
            {
                AVAudioApplication.RequestRecordPermission(_ => { });
            }
        }
        else if (_session.RecordPermission == AVAudioSessionRecordPermission.Undetermined)
        {
            _session.RequestRecordPermission(_ => { });
        }
    }

    public void StartCapture(AudioStreamRequest request, Action<short[]> samples, Action<string> error)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_captureStarted)
            {
                return;
            }

            EnsureConfigured(request.SampleRate);
            var input = _engine.InputNode;
            var inputFormat = input.GetBusOutputFormat(0);
            var converter = new AVAudioConverter(inputFormat, _callFormat!);
            input.InstallTapOnBus(0, 1024, inputFormat, (buffer, _) =>
            {
                try
                {
                    var ratio = _callFormat!.SampleRate / inputFormat.SampleRate;
                    var capacity = Math.Max(1u, (uint)Math.Ceiling(buffer.FrameLength * ratio) + 8u);
                    using var converted = new AVAudioPcmBuffer(_callFormat, capacity);
                    if (!converter.ConvertToBuffer(converted, buffer, out var conversionError))
                    {
                        error(conversionError?.LocalizedDescription ?? "iOS microphone conversion failed.");
                        return;
                    }

                    var count = checked((int)converted.FrameLength);
                    if (count == 0)
                    {
                        return;
                    }

                    var channel = Marshal.ReadIntPtr(converted.Int16ChannelData);
                    var pcm = new short[count];
                    Marshal.Copy(channel, pcm, 0, count);
                    samples(pcm);
                }
                catch (Exception ex)
                {
                    error(ex.Message);
                }
            });

            _captureStarted = true;
            StartEngine();
        }
    }

    public void StopCapture()
    {
        lock (_gate)
        {
            if (!_captureStarted)
            {
                return;
            }

            _engine.InputNode.RemoveTapOnBus(0);
            _captureStarted = false;
            StopEngineWhenIdle();
        }
    }

    public void StartPlayback(AudioStreamRequest request)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_playbackStarted)
            {
                return;
            }

            EnsureConfigured(request.SampleRate);
            _playbackStarted = true;
            StartEngine();
            _player.Play();
        }
    }

    public void SchedulePlayback(ReadOnlySpan<short> pcm, Action completed)
    {
        AVAudioPcmBuffer buffer;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_playbackStarted || _callFormat is null)
            {
                return;
            }

            buffer = new AVAudioPcmBuffer(_callFormat, checked((uint)pcm.Length))
            {
                FrameLength = checked((uint)pcm.Length),
            };
            var channel = Marshal.ReadIntPtr(buffer.Int16ChannelData);
            Marshal.Copy(pcm.ToArray(), 0, channel, pcm.Length);
        }

        _player.ScheduleBuffer(buffer, () =>
        {
            buffer.Dispose();
            completed();
        });
    }

    public void StopPlayback()
    {
        lock (_gate)
        {
            if (!_playbackStarted)
            {
                return;
            }

            _player.Stop();
            _playbackStarted = false;
            StopEngineWhenIdle();
        }
    }

    public void SetSpeakerEnabled(bool enabled)
    {
        if (!_session.OverrideOutputAudioPort(
                enabled ? AVAudioSessionPortOverride.Speaker : AVAudioSessionPortOverride.None,
                out var error))
        {
            throw new AudioDeviceException(error?.LocalizedDescription ?? "iOS could not change the call audio route.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_captureStarted)
            {
                _engine.InputNode.RemoveTapOnBus(0);
            }

            _captureStarted = false;
            _playbackStarted = false;
            _player.Stop();
            _engine.Stop();
            if (_playerAttached)
            {
                _engine.DetachNode(_player);
            }

            _ = _session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation);
            _callFormat?.Dispose();
            _player.Dispose();
            _engine.Dispose();
            _disposed = true;
        }
    }

    private void EnsureConfigured(int sampleRate)
    {
        if (_callFormat is not null)
        {
            if (Math.Abs(_callFormat.SampleRate - sampleRate) > 0.5)
            {
                throw new AudioDeviceException("The iOS call audio streams must use the same sample rate.");
            }

            return;
        }

        var categoryError = _session.SetCategory(
            AVAudioSessionCategory.PlayAndRecord,
            AVAudioSessionMode.VoiceChat,
            AVAudioSessionCategoryOptions.AllowBluetooth);
        if (categoryError is not null)
        {
            throw new AudioDeviceException(categoryError.LocalizedDescription);
        }

        if (!_session.SetPreferredSampleRate(sampleRate, out var sampleRateError))
        {
            throw new AudioDeviceException(sampleRateError?.LocalizedDescription ?? "iOS rejected the call sample rate.");
        }

        if (!_session.SetPreferredIOBufferDuration(0.02, out var bufferError))
        {
            throw new AudioDeviceException(bufferError?.LocalizedDescription ?? "iOS rejected the call buffer duration.");
        }

        var activeError = _session.SetActive(true);
        if (activeError is not null)
        {
            throw new AudioDeviceException(activeError.LocalizedDescription);
        }

        _callFormat = new AVAudioFormat(AVAudioCommonFormat.PCMInt16, sampleRate, 1, false);
        _engine.AttachNode(_player);
        _engine.Connect(_player, _engine.MainMixerNode, _callFormat);
        _playerAttached = true;
    }

    private void StartEngine()
    {
        if (_engine.Running)
        {
            return;
        }

        var activeError = _session.SetActive(true);
        if (activeError is not null)
        {
            throw new AudioDeviceException(activeError.LocalizedDescription);
        }

        _engine.Prepare();
        if (!_engine.StartAndReturnError(out var error))
        {
            throw new AudioDeviceException(error?.LocalizedDescription ?? "iOS could not start call audio.");
        }
    }

    private void StopEngineWhenIdle()
    {
        if (_captureStarted || _playbackStarted)
        {
            return;
        }

        _engine.Stop();
        _ = _session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation);
    }
}

internal sealed class IosAudioCaptureStream : IAudioCaptureStream
{
    private readonly IosAudioEngineHost _host;
    private readonly object _gate = new();
    private short[] _frame;
    private int _filled;
    private bool _started;
    private bool _disposed;

    public IosAudioCaptureStream(IosAudioEngineHost host, AudioStreamRequest request)
    {
        _host = host;
        Request = request;
        _frame = new short[request.FrameSizeSamples];
    }

    public AudioStreamRequest Request { get; }
    public event Action<short[]>? FrameCaptured;
    public event Action<string>? Error;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _host.StartCapture(Request, OnSamples, message => Error?.Invoke(message));
        _started = true;
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _host.StopCapture();
    }

    public void Dispose()
    {
        Stop();
        _disposed = true;
    }

    private void OnSamples(short[] samples)
    {
        var offset = 0;
        while (offset < samples.Length)
        {
            short[]? complete = null;
            lock (_gate)
            {
                var count = Math.Min(_frame.Length - _filled, samples.Length - offset);
                Array.Copy(samples, offset, _frame, _filled, count);
                offset += count;
                _filled += count;
                if (_filled == _frame.Length)
                {
                    complete = _frame;
                    _frame = new short[Request.FrameSizeSamples];
                    _filled = 0;
                }
            }

            if (complete is not null)
            {
                FrameCaptured?.Invoke(complete);
            }
        }
    }
}

internal sealed class IosAudioPlaybackStream : IAudioPlaybackStream
{
    private readonly IosAudioEngineHost _host;
    private int _queuedSamples;
    private bool _started;
    private bool _disposed;

    public IosAudioPlaybackStream(IosAudioEngineHost host, AudioStreamRequest request)
    {
        _host = host;
        Request = request;
    }

    public AudioStreamRequest Request { get; }
    public event Action<string>? Error;
    public TimeSpan Buffered => TimeSpan.FromSeconds((double)Math.Max(0, Volatile.Read(ref _queuedSamples)) / Request.SampleRate);

    public void Enqueue(ReadOnlySpan<short> pcm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started || pcm.IsEmpty)
        {
            return;
        }

        var count = pcm.Length;
        Interlocked.Add(ref _queuedSamples, count);
        try
        {
            _host.SchedulePlayback(pcm, () => Interlocked.Add(ref _queuedSamples, -count));
        }
        catch (Exception ex)
        {
            Interlocked.Add(ref _queuedSamples, -count);
            Error?.Invoke(ex.Message);
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _host.StartPlayback(Request);
        _started = true;
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _host.StopPlayback();
        Interlocked.Exchange(ref _queuedSamples, 0);
    }

    public void Dispose()
    {
        Stop();
        _disposed = true;
    }
}
