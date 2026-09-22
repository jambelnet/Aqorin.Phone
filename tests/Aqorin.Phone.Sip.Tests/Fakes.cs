using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Model;
using Aqorin.Phone.Sip.Internal;

namespace Aqorin.Phone.Sip.Tests;

internal sealed class FakeTransportHandle(SipAccountSettings settings, System.Net.IPEndPoint? registrar = null) : ISipTransportHandle
{
    public SipAccountSettings Settings { get; } = settings;
    public System.Net.IPEndPoint? Registrar { get; } = registrar;
    public System.Net.IPEndPoint? LocalContactEndPoint { get; } = new(System.Net.IPAddress.Loopback, 5060);
    public string Description => $"fake {Settings.Transport}";
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}

internal sealed class FakeTransportFactory : ISipTransportFactory
{
    public List<FakeTransportHandle> Created { get; } = [];
    public Exception? ThrowOnCreate { get; set; }

    public ISipTransportHandle Create(SipAccountSettings settings, System.Net.IPEndPoint? registrar)
    {
        if (ThrowOnCreate is not null)
        {
            throw ThrowOnCreate;
        }

        var handle = new FakeTransportHandle(settings, registrar);
        Created.Add(handle);
        return handle;
    }
}

internal sealed class FakeRegistrationClient : ISipRegistrationClient
{
    public event Action<RegistrationEvent>? Completed;
    public event Action? Removed;
    public bool IsRegistered { get; set; }
    public int StartCalls { get; private set; }
    public List<bool> StopCalls { get; } = [];
    public bool Disposed { get; private set; }
    public RegistrationEvent? OnStart { get; set; }

    public void Start()
    {
        StartCalls++;
        if (OnStart is not null)
        {
            Raise(OnStart);
        }
    }

    public void Stop(bool sendUnregister)
    {
        StopCalls.Add(sendUnregister);
        if (sendUnregister && IsRegistered)
        {
            IsRegistered = false;
            Removed?.Invoke();
        }
    }

    public void Raise(RegistrationEvent evt)
    {
        IsRegistered = evt.Outcome == RegistrationOutcome.Success;
        Completed?.Invoke(evt);
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeRegistrationClientFactory : ISipRegistrationClientFactory
{
    public Queue<RegistrationEvent?> ScriptedOutcomes { get; } = new();
    public List<FakeRegistrationClient> Created { get; } = [];
    public List<SipAccount> Accounts { get; } = [];

    public ISipRegistrationClient Create(ISipTransportHandle transport, SipAccount account, TimeSpan attemptTimeout)
    {
        Accounts.Add(account);
        var client = new FakeRegistrationClient { OnStart = ScriptedOutcomes.Count > 0 ? ScriptedOutcomes.Dequeue() : null };
        Created.Add(client);
        return client;
    }
}

internal sealed class FakeIncomingCall(string from, string? displayName = null) : ISipIncomingCall
{
    public string CallId { get; } = Guid.NewGuid().ToString("N");
    public string FromUri { get; } = from;
    public string? FromDisplayName { get; } = displayName;
    public event Action? Cancelled;
    public bool RingingStarted { get; private set; }
    public bool AnswerResult { get; set; } = true;
    public IAudioMediaSession? AnsweredWith { get; private set; }
    public (int Code, string Reason)? Rejected { get; private set; }

    public void StartRinging() => RingingStarted = true;

    public Task<bool> AnswerAsync(IAudioMediaSession mediaSession)
    {
        AnsweredWith = mediaSession;
        return Task.FromResult(AnswerResult);
    }

    public void Reject(int statusCode, string reasonPhrase) => Rejected = (statusCode, reasonPhrase);

    public void RaiseCancelled() => Cancelled?.Invoke();
}

internal sealed class FakeUserAgent : ISipUserAgent
{
    public event Action<ISipIncomingCall>? IncomingCall;
    public event Action? Ringing;
    public event Action? Answered;
    public event Action<SipCallFailure>? Failed;
    public event Action? Hungup;

    public bool IsCallActive { get; set; }
    public TaskCompletionSource<bool>? PendingCall { get; private set; }
    public string? LastDestination { get; private set; }
    public SipAccount? LastAccount { get; private set; }
    public IAudioMediaSession? LastMedia { get; private set; }
    public int CancelCalls { get; private set; }
    public int HangupCalls { get; private set; }
    public List<ISipIncomingCall> BusyRejections { get; } = [];
    public bool Disposed { get; private set; }

    public Task<bool> CallAsync(string destinationUri, SipAccount account, IAudioMediaSession mediaSession, int ringTimeoutSeconds)
    {
        LastDestination = destinationUri;
        LastAccount = account;
        LastMedia = mediaSession;
        PendingCall = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return PendingCall.Task;
    }

    public void Cancel()
    {
        CancelCalls++;
        Failed?.Invoke(new SipCallFailure(487, "Request Terminated", "Call cancelled"));
        PendingCall?.TrySetResult(false);
    }

    public void Hangup()
    {
        HangupCalls++;
        IsCallActive = false;
        Hungup?.Invoke();
    }

    public void RejectBusy(ISipIncomingCall call) => BusyRejections.Add(call);

    public void Dispose() => Disposed = true;

    // --- remote side simulation ---
    public void RemoteRinging() => Ringing?.Invoke();

    public void RemoteAnswers()
    {
        IsCallActive = true;
        Answered?.Invoke();
        PendingCall?.TrySetResult(true);
    }

    public void RemoteFails(int status, string reason)
    {
        Failed?.Invoke(new SipCallFailure(status, reason, $"Call failed {status} {reason}"));
        PendingCall?.TrySetResult(false);
    }

    public void RemoteHangsUp()
    {
        IsCallActive = false;
        Hungup?.Invoke();
    }

    public void RemoteCalls(FakeIncomingCall call) => IncomingCall?.Invoke(call);
}

internal sealed class FakeUserAgentFactory : ISipUserAgentFactory
{
    public List<FakeUserAgent> Created { get; } = [];
    public FakeUserAgent Current => Created[^1];

    public ISipUserAgent Create(ISipTransportHandle transport)
    {
        var agent = new FakeUserAgent();
        Created.Add(agent);
        return agent;
    }
}

internal sealed class FakeMediaSession : IAudioMediaSession
{
    public string? NegotiatedCodec { get; set; } = "PCMA";
    public bool IsStarted { get; set; }
    public bool IsClosed { get; private set; }
    public bool IsMuted { get; private set; }
    public bool IsHeld { get; private set; }
    public bool IsSpeakerEnabled { get; private set; }
    public bool Disposed { get; private set; }
    public string? CloseReason { get; private set; }
    public string? OutputDeviceId { get; private set; }
    public event Action<string>? AudioError;
    public event Action<string>? CodecNegotiated;

    public Task SetMutedAsync(bool muted)
    {
        IsMuted = muted;
        return Task.CompletedTask;
    }

    public Task SetHeldAsync(bool held)
    {
        IsHeld = held;
        return Task.CompletedTask;
    }

    public Task SetOutputDeviceAsync(string? outputDeviceId)
    {
        OutputDeviceId = outputDeviceId;
        return Task.CompletedTask;
    }

    public Task SetSpeakerEnabledAsync(bool enabled)
    {
        IsSpeakerEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task CloseAsync(string reason)
    {
        IsClosed = true;
        CloseReason = reason;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public void RaiseAudioError(string message) => AudioError?.Invoke(message);
    public void RaiseCodec(string codec)
    {
        NegotiatedCodec = codec;
        CodecNegotiated?.Invoke(codec);
    }
}

internal sealed class FakeMediaSessionFactory : IAudioMediaSessionFactory
{
    public List<FakeMediaSession> Created { get; } = [];
    public List<AudioMediaSessionOptions> Options { get; } = [];

    public IAudioMediaSession Create(AudioMediaSessionOptions? options = null)
    {
        var session = new FakeMediaSession();
        Created.Add(session);
        Options.Add(options ?? new AudioMediaSessionOptions());
        return session;
    }
}

internal static class Wait
{
    public static async Task Until(Func<bool> condition, int timeoutMs = 3000, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met: " + (what ?? "unnamed"));
            }

            await Task.Delay(10);
        }
    }
}

internal sealed class FakeRegistrarResolver : IRegistrarResolver
{
    public Dictionary<string, System.Net.IPAddress[]> Answers { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fritz.box"] = [System.Net.IPAddress.Parse("192.168.178.1")],
    };

    public List<string> Queries { get; } = [];

    public Task<System.Net.IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        Queries.Add(host);
        if (System.Net.IPAddress.TryParse(host, out var literal))
        {
            return Task.FromResult<System.Net.IPAddress[]>([literal]);
        }

        return Task.FromResult(Answers.TryGetValue(host, out var a) ? a : []);
    }
}
