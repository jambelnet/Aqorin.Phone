using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Model;
using Aqorin.Phone.Core.Retry;
using Aqorin.Phone.Sip.Internal;
using Aqorin.Phone.Sip.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aqorin.Phone.Sip.Tests.Loopback;

/// <summary>
/// Audio backend whose device operations misbehave: opening takes a while and stopping/closing never returns.
/// Real drivers occasionally do exactly this; it must never stall SIP signalling.
/// </summary>
internal sealed class HangingAudioDeviceService(TimeSpan openDelay) : IAudioDeviceService
{
    public AudioBackendInfo Backend { get; } = new("Hanging", "test", true, null);
    public int Opened { get; private set; }
    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => [];
    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => [];

    public IAudioCaptureStream OpenCapture(AudioStreamRequest request)
    {
        Thread.Sleep(openDelay);
        Opened++;
        return new Capture(request);
    }

    public IAudioPlaybackStream OpenPlayback(AudioStreamRequest request)
    {
        Thread.Sleep(openDelay);
        Opened++;
        return new Playback(request);
    }

    public void Dispose() { }

    // Long enough to exceed the close timeout under test, short enough not to starve the thread pool for other tests.
    private static void HangForever() => new ManualResetEventSlim(false).Wait(TimeSpan.FromSeconds(3));

    private sealed class Capture(AudioStreamRequest request) : IAudioCaptureStream
    {
        public AudioStreamRequest Request { get; } = request;
        public event Action<short[]>? FrameCaptured { add { } remove { } }
        public event Action<string>? Error { add { } remove { } }
        public void Start() { }
        public void Stop() => HangForever();
        public void Dispose() => HangForever();
    }

    private sealed class Playback(AudioStreamRequest request) : IAudioPlaybackStream
    {
        public AudioStreamRequest Request { get; } = request;
        public event Action<string>? Error { add { } remove { } }
        public TimeSpan Buffered => TimeSpan.Zero;
        public void Enqueue(ReadOnlySpan<short> pcm) { }
        public void Start() { }
        public void Stop() => HangForever();
        public void Dispose() => HangForever();
    }
}

[Trait("Category", "Loopback")]
[Collection("Loopback")] // real UDP sockets and timing: run these classes one at a time
public sealed class HangingAudioDeviceTests : IAsyncLifetime
{
    private readonly FakeFritzBox _box = FakeFritzBox.Start();
    private readonly SipSessionContext _context = new();
    private readonly HangingAudioDeviceService _audio = new(TimeSpan.FromMilliseconds(300));
    private SipRegistrationService _registration = null!;
    private SipCallService _calls = null!;

    public async Task InitializeAsync()
    {
        AppAudioSource.CloseTimeout = TimeSpan.FromMilliseconds(400);
        AppAudioSource.OpenTimeout = TimeSpan.FromSeconds(3);

        _registration = new SipRegistrationService(
            new SipSorceryTransportFactory(NullLogger<SipSorceryTransportFactory>.Instance, new SipDiagnosticsOptions()),
            new SipSorceryRegistrationClientFactory(NullLogger<SipSorceryRegistrationClient>.Instance),
            _context,
            NullLogger<SipRegistrationService>.Instance,
            new ExponentialBackoffPolicy(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), maxAttempts: 0),
            attemptTimeout: TimeSpan.FromSeconds(5))
        {
            ReRegisterOnNetworkChange = false,
            GuardGrace = TimeSpan.FromSeconds(2),
        };
        _calls = new SipCallService(
            new SipSorceryUserAgentFactory(NullLogger<SipSorceryUserAgent>.Instance),
            new SipSorceryAudioMediaSessionFactory(_audio, NullLogger<SipSorceryAudioMediaSessionFactory>.Instance),
            _context,
            NullLogger<SipCallService>.Instance);

        _box.Behaviour = InviteBehaviour.RingThenAnswer;
        _box.RingDuration = TimeSpan.FromMilliseconds(100);
        await _registration.RegisterAsync(new SipAccount(
            new SipAccountSettings { Registrar = _box.Host, Port = _box.Port, Username = _box.Username, RegistrationExpirySeconds = 300 },
            _box.Password));
        Assert.Equal(RegistrationState.Registered, _registration.Status.State);
    }

    public async Task DisposeAsync()
    {
        await _calls.DisposeAsync();
        await _registration.DisposeAsync();
        await _box.DisposeAsync();
        AppAudioSource.CloseTimeout = TimeSpan.FromSeconds(5);
        AppAudioSource.OpenTimeout = TimeSpan.FromSeconds(10);
    }

    private static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Hanging_audio_devices_do_not_stall_hangup_or_the_next_call()
    {
        var first = await _calls.PlaceCallAsync("0301111").WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(CallState.Active, first.State);
        await WaitFor(() => _audio.Opened == 2, "mic and speaker opened");

        // Remote hang-up: processed on SIPSorcery's transport thread. Device teardown hangs, signalling must not.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _box.HangUpSoftphone();
        await WaitFor(() => _calls.CurrentCall.State == CallState.Idle, "BYE processed", timeoutMs: 5000);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"hang-up took {sw.Elapsed}");

        // The very next call must still get through the same transport.
        var second = await _calls.PlaceCallAsync("0302222").WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(CallState.Active, second.State);
        Assert.Equal(2, _box.AcceptedInvites);

        sw.Restart();
        await _calls.HangupAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CallState.Idle, _calls.CurrentCall.State);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"local hang-up took {sw.Elapsed}");
        await WaitFor(() => _box.ByesReceived == 1, "BYE sent");
    }
}
