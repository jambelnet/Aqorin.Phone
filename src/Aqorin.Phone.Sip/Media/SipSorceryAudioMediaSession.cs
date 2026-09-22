using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Sip.Internal;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace Aqorin.Phone.Sip.Media;

/// <summary>Per-call RTP audio session: <see cref="VoIPMediaSession"/> wired to the application's audio devices.</summary>
internal sealed class SipSorceryAudioMediaSession : IAudioMediaSession, ISipSorceryMediaSessionProvider
{
    private readonly AppAudioSource _source;
    private readonly AppAudioSink _sink;
    private readonly IAudioDeviceService _devices;
    private readonly AudioMediaSessionOptions _options;
    private readonly VoIPMediaSession _session;
    private readonly ILogger _logger;
    private readonly object _controlGate = new();
    private bool _muted;
    private bool _held;
    private int _closed;

    public SipSorceryAudioMediaSession(IAudioDeviceService devices, AudioMediaSessionOptions options, ILogger logger)
    {
        _devices = devices;
        _options = options;
        _logger = logger;
        _source = new AppAudioSource(devices, options, logger);
        _sink = new AppAudioSink(devices, options, logger);
        _source.OnAudioSourceError += message => AudioError?.Invoke(message);
        _sink.OnAudioSinkError += message => AudioError?.Invoke(message);

        _session = new VoIPMediaSession(new VoIPMediaSessionConfig
        {
            MediaEndPoint = new MediaEndPoints { AudioSource = _source, AudioSink = _sink },
        });
        _session.OnAudioFormatsNegotiated += OnFormatsNegotiated;
        _session.OnClosed += () => Interlocked.Exchange(ref _closed, 1);
        _session.OnTimeout += media => _logger.LogWarning("RTP timeout on {Media} stream.", media);

        if (!devices.Backend.IsAvailable)
        {
            _logger.LogWarning("Audio backend unavailable: {Reason}. The call will have no audio.", devices.Backend.UnavailableReason);
        }
    }

    public IMediaSession MediaSession => _session;

    public string? NegotiatedCodec { get; private set; }

    public bool IsStarted => _session.IsAudioStarted;

    public bool IsClosed => _session.IsClosed || Volatile.Read(ref _closed) == 1;

    public event Action<string>? AudioError;

    public event Action<string>? CodecNegotiated;

    public Task SetMutedAsync(bool muted)
    {
        lock (_controlGate)
        {
            _muted = muted;
            return ApplyAudioControlsAsync();
        }
    }

    public Task SetHeldAsync(bool held)
    {
        lock (_controlGate)
        {
            _held = held;
            return ApplyAudioControlsAsync();
        }
    }

    public Task SetOutputDeviceAsync(string? outputDeviceId) => _sink.SetOutputDeviceAsync(outputDeviceId);

    public Task SetSpeakerEnabledAsync(bool enabled)
    {
        if (_devices is ICallAudioRouteController routes)
        {
            return routes.SetSpeakerEnabledAsync(enabled);
        }

        return _sink.SetOutputDeviceAsync(enabled ? null : _options.OutputDeviceId);
    }

    /// <summary>
    /// Closes RTP and releases the audio devices. Never blocks the caller on native device code: device teardown
    /// runs on the thread pool and is bounded by <see cref="AppAudioSource.CloseTimeout"/>.
    /// </summary>
    public Task CloseAsync(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            try
            {
                if (!_session.IsClosed)
                {
                    _session.Close(reason);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Closing the RTP session failed.");
            }
        }

        return Task.WhenAll(_source.CloseAsync(), _sink.CloseAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync("disposed").ConfigureAwait(false);
        _session.OnAudioFormatsNegotiated -= OnFormatsNegotiated;
        try
        {
            _session.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing the RTP session failed.");
        }
    }

    private void OnFormatsNegotiated(List<AudioFormat> formats)
    {
        var format = formats.FirstOrDefault();
        if (format.IsEmpty())
        {
            return;
        }

        NegotiatedCodec = format.FormatName;
        _logger.LogInformation("Negotiated audio codec {Codec} (payload type {Id}).", format.FormatName, format.FormatID);
        CodecNegotiated?.Invoke(format.FormatName);
    }

    private Task ApplyAudioControlsAsync()
    {
        var source = _muted || _held ? _source.PauseAudio() : _source.ResumeAudio();
        var sink = _held ? _sink.PauseAudioSink() : _sink.ResumeAudioSink();
        return Task.WhenAll(source, sink);
    }
}

/// <summary>Creates media sessions bound to the registered <see cref="IAudioDeviceService"/>.</summary>
public sealed class SipSorceryAudioMediaSessionFactory(IAudioDeviceService devices, ILogger<SipSorceryAudioMediaSessionFactory> logger) : IAudioMediaSessionFactory
{
    public IAudioMediaSession Create(AudioMediaSessionOptions? options = null) =>
        new SipSorceryAudioMediaSession(devices, options ?? new AudioMediaSessionOptions(), logger);
}
