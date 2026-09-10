using Aqorin.Phone.Core.Abstractions;
using Microsoft.Extensions.Logging;
using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;

namespace Aqorin.Phone.Audio.PortAudio;

/// <summary>
/// Shared open/start/stop/dispose logic for PortAudio callback streams.
/// Tries the requested sample rate first; if the device refuses it (common on macOS for 8 kHz),
/// re-opens at the device's default rate and lets the derived class resample.
/// </summary>
internal abstract class PortAudioStreamBase : IDisposable
{
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private PaStream? _stream;
    private PaStream.Callback? _callback; // keep the delegate alive for the native side
    private bool _disposed;

    protected PortAudioStreamBase(AudioStreamRequest request, int deviceIndex, DeviceInfo deviceInfo, bool isInput, ILogger logger)
    {
        Request = request;
        DeviceIndex = deviceIndex;
        DeviceName = deviceInfo.name;
        _logger = logger;

        var candidates = new List<double> { request.SampleRate };
        if (deviceInfo.defaultSampleRate > 0 && Math.Abs(deviceInfo.defaultSampleRate - request.SampleRate) > 0.5)
        {
            candidates.Add(deviceInfo.defaultSampleRate);
        }

        foreach (var rate in new[] { 48000d, 44100d, 16000d })
        {
            if (!candidates.Contains(rate))
            {
                candidates.Add(rate);
            }
        }

        Exception? last = null;
        foreach (var rate in candidates)
        {
            try
            {
                var parameters = new StreamParameters
                {
                    device = deviceIndex,
                    channelCount = 1,
                    sampleFormat = SampleFormat.Int16,
                    suggestedLatency = isInput ? deviceInfo.defaultLowInputLatency : deviceInfo.defaultLowOutputLatency,
                    hostApiSpecificStreamInfo = IntPtr.Zero,
                };

                var framesPerBuffer = (uint)Math.Max(1, (int)Math.Round(rate * request.FrameDurationMilliseconds / 1000.0));
                _callback = OnNativeCallback;
                _stream = new PaStream(
                    isInput ? parameters : null,
                    isInput ? null : parameters,
                    rate,
                    framesPerBuffer,
                    StreamFlags.ClipOff,
                    _callback,
                    IntPtr.Zero);
                DeviceSampleRate = (int)Math.Round(rate);
                _logger.LogInformation("Opened PortAudio {Direction} stream on '{Device}' at {Rate} Hz ({Frames} frames/buffer).",
                    isInput ? "capture" : "playback", DeviceName, DeviceSampleRate, framesPerBuffer);
                return;
            }
            catch (PortAudioException ex)
            {
                last = ex;
                _logger.LogDebug("Device '{Device}' rejected {Rate} Hz: {Error}", DeviceName, rate, ex.Message);
            }
        }

        throw new AudioDeviceException($"Could not open the {(isInput ? "microphone" : "speaker")} '{DeviceName}'. {last?.Message}", last);
    }

    public AudioStreamRequest Request { get; }

    public int DeviceIndex { get; }

    public string DeviceName { get; }

    /// <summary>Rate the device is actually running at (equals the request when the device accepted it).</summary>
    public int DeviceSampleRate { get; private set; }

    public event Action<string>? Error;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stream is { IsActive: false })
            {
                try
                {
                    _stream.Start();
                }
                catch (PortAudioException ex)
                {
                    throw new AudioDeviceException($"Could not start audio on '{DeviceName}': {ex.Message}", ex);
                }
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed || _stream is null)
            {
                return;
            }

            try
            {
                if (_stream.IsActive)
                {
                    _stream.Stop();
                }
            }
            catch (PortAudioException ex)
            {
                _logger.LogDebug(ex, "Stopping PortAudio stream on '{Device}' failed.", DeviceName);
            }
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

            _disposed = true;
            try
            {
                if (_stream is not null)
                {
                    if (_stream.IsActive)
                    {
                        _stream.Abort();
                    }

                    _stream.Close();
                    _stream.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Disposing PortAudio stream on '{Device}' failed.", DeviceName);
            }
            finally
            {
                _stream = null;
                _callback = null;
            }
        }
    }

    protected abstract StreamCallbackResult OnAudio(IntPtr input, IntPtr output, int frameCount, StreamCallbackFlags flags);

    protected void RaiseError(string message)
    {
        _logger.LogError("Audio error on '{Device}': {Message}", DeviceName, message);
        Error?.Invoke(message);
    }

    private StreamCallbackResult OnNativeCallback(IntPtr input, IntPtr output, uint frameCount, ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userDataPtr)
    {
        // Never let an exception cross the native boundary.
        try
        {
            if (_disposed)
            {
                return StreamCallbackResult.Complete;
            }

            return OnAudio(input, output, (int)frameCount, statusFlags);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in audio callback on '{Device}'.", DeviceName);
            Error?.Invoke(ex.Message);
            return StreamCallbackResult.Abort;
        }
    }
}
