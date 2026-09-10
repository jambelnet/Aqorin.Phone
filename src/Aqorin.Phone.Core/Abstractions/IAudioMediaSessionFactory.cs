namespace Aqorin.Phone.Core.Abstractions;

/// <summary>Audio codecs the softphone offers, in preference order.</summary>
public enum AudioCodec
{
    /// <summary>G.711 A-law (payload type 8) — common on European router and PBX lines.</summary>
    Pcma,
    /// <summary>G.711 µ-law (payload type 0).</summary>
    Pcmu
}

public sealed record AudioMediaSessionOptions
{
    public IReadOnlyList<AudioCodec> CodecPreference { get; init; } = [AudioCodec.Pcma, AudioCodec.Pcmu];
    public string? InputDeviceId { get; init; }
    public string? OutputDeviceId { get; init; }
    public int FrameDurationMilliseconds { get; init; } = 20;
}

/// <summary>
/// A per-call audio media session (RTP + codecs + devices) behind an application-owned interface.
/// The concrete type belongs to the SIP layer; the UI only ever sees this interface.
/// </summary>
public interface IAudioMediaSession : IAsyncDisposable
{
    /// <summary>Negotiated codec name (e.g. "PCMA") once known, otherwise null.</summary>
    string? NegotiatedCodec { get; }

    bool IsStarted { get; }

    bool IsClosed { get; }

    /// <summary>Raised when the microphone or speaker fails during the call.</summary>
    event Action<string>? AudioError;

    /// <summary>Raised when RTP negotiation completes.</summary>
    event Action<string>? CodecNegotiated;

    Task SetMutedAsync(bool muted);

    Task SetHeldAsync(bool held);

    Task SetOutputDeviceAsync(string? outputDeviceId);

    /// <summary>Closes RTP and releases audio devices. Idempotent.</summary>
    Task CloseAsync(string reason);
}

/// <summary>Creates one <see cref="IAudioMediaSession"/> per call.</summary>
public interface IAudioMediaSessionFactory
{
    IAudioMediaSession Create(AudioMediaSessionOptions? options = null);
}
