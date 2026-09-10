using Aqorin.Phone.Audio;
using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Diagnostics;
using Aqorin.Phone.Core.Model;
using Aqorin.Phone.Sip;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Aqorin.Phone.IntegrationTests;

/// <summary>
/// Live tests against a FRITZ!Box. Run with:
/// <code>
/// FRITZ_SIP_HOST=fritz.box FRITZ_SIP_USERNAME=620 FRITZ_SIP_PASSWORD=... FRITZ_SIP_DESTINATION=**1 \
///   dotnet test tests/Aqorin.Phone.IntegrationTests --filter Category=Integration
/// </code>
/// All output is redacted; the password never appears in logs.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FritzBoxIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private ServiceProvider? _services;

    public FritzBoxIntegrationTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        if (FritzEnvironment.MissingVariables(false).Any())
        {
            return Task.CompletedTask;
        }

        var services = new ServiceCollection();
        var log = new DiagnosticsLog { MinimumLevel = LogLevel.Debug };
        log.EntryAdded += e => _output.WriteLine(e.ToString());
        services.AddSingleton(log);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(new DiagnosticsLoggerProvider(log)));
        services.AddPortAudio(allowNullFallback: true); // CI boxes have no sound card; signalling still works
        services.AddSipSorceryTelephony();
        _services = services.BuildServiceProvider();
        SipServiceCollectionExtensions.UseSipSorceryLogging(_services.GetRequiredService<ILoggerFactory>());
        _services.GetRequiredService<SipDiagnosticsOptions>().SipTraceEnabled = true;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_services is null)
        {
            return;
        }

        await _services.GetRequiredService<ICallService>().DisposeAsync();
        await _services.GetRequiredService<ISipRegistrationService>().DisposeAsync();
        await _services.DisposeAsync();
    }

    [IntegrationFact]
    public async Task Registers_and_unregisters_with_the_fritzbox()
    {
        var registration = _services!.GetRequiredService<ISipRegistrationService>();
        var statuses = new List<RegistrationStatus>();
        registration.StatusChanged += (_, s) => statuses.Add(s);

        await registration.RegisterAsync(FritzEnvironment.Account());
        await WaitFor(() => registration.Status.State is RegistrationState.Registered or RegistrationState.Failed, TimeSpan.FromSeconds(30));

        Assert.True(registration.Status.IsRegistered, $"Registration failed: {registration.Status.Message} {registration.Status.Detail}");
        Assert.NotNull(registration.Status.ExpiresAt);

        await registration.UnregisterAsync();
        Assert.Equal(RegistrationState.Disconnected, registration.Status.State);
        Assert.Contains(statuses, s => s.State == RegistrationState.Registered);
    }

    [IntegrationFact]
    public async Task Wrong_password_yields_an_authentication_failure_without_retries()
    {
        var registration = _services!.GetRequiredService<ISipRegistrationService>();
        var account = FritzEnvironment.Account();
        var wrong = account with { Password = account.Password + "-wrong" };

        await registration.RegisterAsync(wrong);
        await WaitFor(() => registration.Status.State is RegistrationState.Registered or RegistrationState.Failed, TimeSpan.FromSeconds(30));

        Assert.Equal(RegistrationState.Failed, registration.Status.State);
        Assert.True(registration.Status.IsAuthenticationFailure, registration.Status.Message);
    }

    [IntegrationFact(requiresDestination: true)]
    public async Task Places_a_call_to_the_destination_and_hangs_up()
    {
        var registration = _services!.GetRequiredService<ISipRegistrationService>();
        var calls = _services!.GetRequiredService<ICallService>();
        var states = new List<CallState>();
        calls.CallChanged += (_, c) => states.Add(c.State);

        await registration.RegisterAsync(FritzEnvironment.Account());
        await WaitFor(() => registration.Status.State is RegistrationState.Registered or RegistrationState.Failed, TimeSpan.FromSeconds(30));
        Assert.True(registration.Status.IsRegistered, registration.Status.Message);

        var destination = Environment.GetEnvironmentVariable(FritzEnvironment.Destination)!;
        var callTask = calls.PlaceCallAsync(destination);

        // Expect the FRITZ!Box to at least start ringing the destination (or answer it) within 20 s.
        await WaitFor(() => calls.CurrentCall.State is CallState.Ringing or CallState.Active or CallState.Failed or CallState.Idle, TimeSpan.FromSeconds(20));
        _output.WriteLine($"Call state after INVITE: {calls.CurrentCall.State} — {calls.CurrentCall.Message} {calls.CurrentCall.Detail}");
        Assert.True(calls.CurrentCall.State is CallState.Ringing or CallState.Active, $"Unexpected: {calls.CurrentCall.Message} {calls.CurrentCall.Detail}");

        if (calls.CurrentCall.State == CallState.Ringing)
        {
            // Give a human a few seconds to pick up; otherwise cancel.
            await Task.WhenAny(callTask, Task.Delay(TimeSpan.FromSeconds(8)));
        }

        if (calls.CurrentCall.IsInProgress)
        {
            if (calls.CurrentCall.State == CallState.Active)
            {
                await Task.Delay(TimeSpan.FromSeconds(3)); // exchange some RTP
                _output.WriteLine($"Codec: {calls.CurrentCall.Codec}");
            }

            await calls.HangupAsync();
        }

        await callTask;
        await WaitFor(() => calls.CurrentCall.IsResting, TimeSpan.FromSeconds(10));
        Assert.Contains(states, s => s == CallState.Dialing);
        await registration.UnregisterAsync();
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for the FRITZ!Box.");
            }

            await Task.Delay(100);
        }
    }
}
