using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Model;
using Aqorin.Phone.Mobile.Services;
using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.Mobile.Platforms.Android;

public sealed class AndroidCallIntegration : IMobileCallIntegration
{
    private readonly ISipRegistrationService _registration;
    private readonly ICallService _calls;
    private readonly ILogger<AndroidCallIntegration> _logger;
    private bool _started;

    public AndroidCallIntegration(
        ISipRegistrationService registration,
        ICallService calls,
        ILogger<AndroidCallIntegration> logger)
    {
        _registration = registration;
        _calls = calls;
        _logger = logger;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        AndroidCallRuntime.Attach(_calls, _logger);
        _registration.StatusChanged += OnRegistrationChanged;
        _calls.CallChanged += OnCallChanged;
        Publish(_registration.Status, _calls.CurrentCall);
    }

    public ValueTask DisposeAsync()
    {
        if (!_started)
        {
            return ValueTask.CompletedTask;
        }

        _started = false;
        _registration.StatusChanged -= OnRegistrationChanged;
        _calls.CallChanged -= OnCallChanged;
        AndroidCallRuntime.Detach(_calls);
        SipForegroundService.Stop();
        return ValueTask.CompletedTask;
    }

    private void OnRegistrationChanged(object? sender, RegistrationStatus status) =>
        Publish(status, _calls.CurrentCall);

    private void OnCallChanged(object? sender, CallInfo call) =>
        Publish(_registration.Status, call);

    private static void Publish(RegistrationStatus registration, CallInfo call)
    {
        if (registration.State is RegistrationState.Registering or RegistrationState.Registered or RegistrationState.Unregistering
            || call.IsInProgress)
        {
            SipForegroundService.StartOrUpdate(registration, call);
        }
        else
        {
            SipForegroundService.Stop();
        }
    }
}

internal static class AndroidCallRuntime
{
    private static readonly object Gate = new();
    private static ICallService? _calls;
    private static ILogger? _logger;

    public static void Attach(ICallService calls, ILogger logger)
    {
        lock (Gate)
        {
            _calls = calls;
            _logger = logger;
        }
    }

    public static void Detach(ICallService calls)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_calls, calls))
            {
                _calls = null;
                _logger = null;
            }
        }
    }

    public static async Task ExecuteAsync(string action)
    {
        ICallService? calls;
        ILogger? logger;
        lock (Gate)
        {
            calls = _calls;
            logger = _logger;
        }

        if (calls is null)
        {
            return;
        }

        try
        {
            switch (action)
            {
                case SipForegroundService.ActionAnswer:
                    await calls.AnswerAsync().ConfigureAwait(false);
                    break;
                case SipForegroundService.ActionReject:
                    await calls.RejectAsync().ConfigureAwait(false);
                    break;
                case SipForegroundService.ActionHangup:
                    await calls.HangupAsync().ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Android notification call action {Action} failed.", action);
        }
    }
}
