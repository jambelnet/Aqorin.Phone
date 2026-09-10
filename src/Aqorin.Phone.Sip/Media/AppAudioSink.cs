using System.Net;
using Aqorin.Phone.Core.Abstractions;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace Aqorin.Phone.Sip.Media;

/// <summary>
/// Bridges SIPSorcery's <see cref="IAudioSink"/> to the application's <see cref="IAudioPlaybackStream"/> (speaker):
/// decodes incoming G.711 RTP payloads to PCM and queues them for playback.
/// Device open/close never runs on the caller's thread (SIPSorcery's SIP transport thread) — see <see cref="AppAudioSource"/>.
/// </summary>
internal sealed class AppAudioSink : IAudioSink, IDisposable
{
    private readonly IAudioDeviceService _devices;
    private readonly AudioMediaSessionOptions _options;
    private readonly ILogger _logger;
    private readonly AudioEncoder _encoder = new();
    private readonly object _gate = new();
    private List<AudioFormat> _formats;
    private FormatBox _format;
    private IAudioPlaybackStream? _playback;
    private string? _outputDeviceId;
    private volatile bool _paused;
    private volatile bool _closed;
    private long _framesPlayed;

    public AppAudioSink(IAudioDeviceService devices, AudioMediaSessionOptions options, ILogger logger)
    {
        _devices = devices;
        _options = options;
        _logger = logger;
        _formats = CodecCatalog.Formats(options.CodecPreference);
        _format = new FormatBox(_formats[0]);
        _outputDeviceId = options.OutputDeviceId;
    }

    public event SourceErrorDelegate? OnAudioSinkError;

    public List<AudioFormat> GetAudioSinkFormats() => new(_formats);

    public void SetAudioSinkFormat(AudioFormat audioFormat)
    {
        Volatile.Write(ref _format, new FormatBox(audioFormat));
        _logger.LogInformation("Audio sink format set to {Codec} @ {Rate} Hz.", audioFormat.FormatName, audioFormat.ClockRate);
    }

    public void RestrictFormats(Func<AudioFormat, bool> filter)
    {
        var filtered = _formats.Where(filter).ToList();
        if (filtered.Count > 0)
        {
            _formats = filtered;
            if (!filtered.Contains(_format.Format))
            {
                SetAudioSinkFormat(filtered[0]);
            }
        }
    }

    public void GotAudioRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[] payload) =>
        Play(payload, Volatile.Read(ref _format).Format);

    public void GotEncodedMediaFrame(EncodedAudioFrame encodedMediaFrame)
    {
        if (encodedMediaFrame?.EncodedAudio is null)
        {
            return;
        }

        var format = encodedMediaFrame.AudioFormat;
        if (format.IsEmpty() || format.Codec == AudioCodecsEnum.Unknown)
        {
            format = Volatile.Read(ref _format).Format;
        }

        Play(encodedMediaFrame.EncodedAudio, format);
    }

    public Task StartAudioSink()
    {
        if (_closed)
        {
            return Task.CompletedTask;
        }

        var open = Task.Factory.StartNew(OpenPlayback, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return Task.WhenAny(open, Task.Delay(AppAudioSource.OpenTimeout)).ContinueWith(_ =>
        {
            if (!open.IsCompleted)
            {
                _logger.LogError("Opening the speaker did not complete within {Timeout}s; continuing without playback.", AppAudioSource.OpenTimeout.TotalSeconds);
                OnAudioSinkError?.Invoke($"Speaker did not respond within {AppAudioSource.OpenTimeout.TotalSeconds:0}s.");
            }
        }, TaskScheduler.Default);
    }

    public Task PauseAudioSink()
    {
        _paused = true;
        return Task.CompletedTask;
    }

    public Task ResumeAudioSink()
    {
        _paused = false;
        return Task.CompletedTask;
    }

    public Task SetOutputDeviceAsync(string? outputDeviceId)
    {
        IAudioPlaybackStream? oldPlayback;
        lock (_gate)
        {
            if (_closed || string.Equals(_outputDeviceId, outputDeviceId, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            _outputDeviceId = outputDeviceId;
            oldPlayback = _playback;
            _playback = null;
        }

        var switchOutput = Task.Factory.StartNew(() =>
        {
            ReleasePlayback(oldPlayback);
            OpenPlayback();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        return Task.WhenAny(switchOutput, Task.Delay(AppAudioSource.CloseTimeout)).ContinueWith(_ =>
        {
            if (!switchOutput.IsCompleted)
            {
                _logger.LogError("Switching the speaker did not complete within {Timeout}s; continuing without playback.", AppAudioSource.CloseTimeout.TotalSeconds);
                OnAudioSinkError?.Invoke($"Speaker switch did not respond within {AppAudioSource.CloseTimeout.TotalSeconds:0}s.");
            }
        }, TaskScheduler.Default);
    }

    public Task CloseAudioSink()
    {
        _ = CloseAsync();
        return Task.CompletedTask;
    }

    public void Dispose() => _ = CloseAsync();

    internal Task CloseAsync()
    {
        IAudioPlaybackStream? playback;
        lock (_gate)
        {
            if (_closed)
            {
                return Task.CompletedTask;
            }

            _closed = true;
            playback = _playback;
            _playback = null;
        }

        var release = Task.Factory.StartNew(() =>
        {
            ReleasePlayback(playback);
            _encoder.Dispose();
            _logger.LogDebug("Speaker playback closed after {Frames} frames.", _framesPlayed);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        return Task.WhenAny(release, Task.Delay(AppAudioSource.CloseTimeout)).ContinueWith(_ =>
        {
            if (!release.IsCompleted)
            {
                _logger.LogError("Closing the speaker did not complete within {Timeout}s; abandoning the device handle.", AppAudioSource.CloseTimeout.TotalSeconds);
            }
        }, TaskScheduler.Default);
    }

    private void OpenPlayback()
    {
        try
        {
            var format = Volatile.Read(ref _format).Format;
            string? outputDeviceId;
            lock (_gate)
            {
                outputDeviceId = _outputDeviceId;
            }

            var request = new AudioStreamRequest(format.ClockRate, _options.FrameDurationMilliseconds, outputDeviceId);
            var playback = _devices.OpenPlayback(request);
            playback.Error += message => OnAudioSinkError?.Invoke("Speaker: " + message);

            lock (_gate)
            {
                if (_closed)
                {
                    playback.Dispose();
                    return;
                }

                _playback = playback;
            }

            playback.Start();
            _logger.LogInformation("Speaker playback started ({Rate} Hz).", request.SampleRate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Speaker could not be started.");
            OnAudioSinkError?.Invoke("Speaker unavailable: " + ex.Message);
        }
    }

    /// <summary>Runs on SIPSorcery's RTP receive thread: decode and enqueue only.</summary>
    private void Play(byte[] payload, AudioFormat format)
    {
        var playback = _playback;
        if (playback is null || _paused || _closed || payload.Length == 0)
        {
            return;
        }

        try
        {
            var pcm = _encoder.DecodeAudio(payload, format);
            if (pcm is { Length: > 0 })
            {
                playback.Enqueue(pcm);
                _framesPlayed++;
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Decoding an RTP audio frame failed.");
            OnAudioSinkError?.Invoke("Audio decoding failed: " + ex.Message);
        }
    }

    private void ReleasePlayback(IAudioPlaybackStream? playback)
    {
        if (playback is null)
        {
            return;
        }

        try
        {
            playback.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stopping the speaker failed.");
        }

        try
        {
            playback.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing the speaker failed.");
        }
    }

    private sealed record FormatBox(AudioFormat Format);
}
