using Aqorin.Phone.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.App.Services;

public sealed class RingtonePlayer : IRingtonePlayer
{
    private const int SampleRate = 48000;
    private const int FrameMilliseconds = 20;
    private const double Amplitude = short.MaxValue * 0.16;
    private readonly IAudioDeviceService _audio;
    private readonly ILogger<RingtonePlayer> _logger;
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private IAudioPlaybackStream? _stream;
    private string? _outputDeviceId;

    public RingtonePlayer(IAudioDeviceService audio, ILogger<RingtonePlayer> logger)
    {
        _audio = audio;
        _logger = logger;
    }

    public bool IsPlaying
    {
        get
        {
            lock (_sync)
            {
                return _stream is not null;
            }
        }
    }

    public void Start(string? outputDeviceId)
    {
        lock (_sync)
        {
            if (_stream is not null && string.Equals(_outputDeviceId, outputDeviceId, StringComparison.Ordinal))
            {
                return;
            }
        }

        Stop();

        var cts = new CancellationTokenSource();
        IAudioPlaybackStream? stream = null;
        try
        {
            stream = _audio.OpenPlayback(new AudioStreamRequest(SampleRate, FrameMilliseconds, outputDeviceId));
            stream.Error += message =>
            {
                _logger.LogWarning("Ringtone playback failed: {Message}", message);
                Stop();
            };
            stream.Start();

            lock (_sync)
            {
                _cts = cts;
                _stream = stream;
                _outputDeviceId = outputDeviceId;
                _ = Task.Run(() => PumpAsync(stream, cts.Token));
            }
        }
        catch (Exception ex) when (ex is AudioDeviceException or InvalidOperationException)
        {
            cts.Dispose();
            stream?.Dispose();
            _logger.LogWarning(ex, "Could not start the incoming call ringtone.");
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        IAudioPlaybackStream? stream;
        lock (_sync)
        {
            cts = _cts;
            stream = _stream;
            _cts = null;
            _stream = null;
            _outputDeviceId = null;
        }

        if (stream is null)
        {
            cts?.Dispose();
            return;
        }

        try
        {
            cts?.Cancel();
            stream.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stopping ringtone playback failed.");
        }
        finally
        {
            stream.Dispose();
            cts?.Dispose();
        }
    }

    public void Dispose() => Stop();

    private async Task PumpAsync(IAudioPlaybackStream stream, CancellationToken cancellationToken)
    {
        var frame = new short[SampleRate * FrameMilliseconds / 1000];
        var phaseA = 0.0;
        var phaseB = 0.0;
        var frameIndex = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var elapsed = frameIndex * FrameMilliseconds % 2600;
                var audible = elapsed < 700 || elapsed is >= 900 and < 1600;
                if (audible)
                {
                    FillTone(frame, ref phaseA, ref phaseB);
                }
                else
                {
                    Array.Clear(frame);
                }

                stream.Enqueue(frame);
                frameIndex++;
                await Task.Delay(FrameMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Incoming call ringtone stopped unexpectedly.");
        }
    }

    private static void FillTone(short[] frame, ref double phaseA, ref double phaseB)
    {
        const double frequencyA = 440.0;
        const double frequencyB = 480.0;
        var stepA = 2.0 * Math.PI * frequencyA / SampleRate;
        var stepB = 2.0 * Math.PI * frequencyB / SampleRate;

        for (var i = 0; i < frame.Length; i++)
        {
            var sample = (Math.Sin(phaseA) + Math.Sin(phaseB)) * Amplitude * 0.5;
            frame[i] = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
            phaseA = WrapPhase(phaseA + stepA);
            phaseB = WrapPhase(phaseB + stepB);
        }
    }

    private static double WrapPhase(double phase) => phase >= 2.0 * Math.PI ? phase - 2.0 * Math.PI : phase;
}
