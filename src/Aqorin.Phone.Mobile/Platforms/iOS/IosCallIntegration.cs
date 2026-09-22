using AVFoundation;
using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Model;
using Aqorin.Phone.Mobile.Services;
using CallKit;
using CoreFoundation;
using Foundation;
using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.Mobile.Platforms.iOS;

/// <summary>Maps the in-process SIP call lifecycle to the native iOS calling surface.</summary>
public sealed class IosCallIntegration : IMobileCallIntegration
{
    private readonly ICallService _calls;
    private readonly ILogger<IosCallIntegration> _logger;
    private readonly CXProvider _provider;
    private readonly ProviderDelegate _delegate;
    private readonly object _gate = new();
    private NSUuid? _nativeCallId;
    private string? _sipCallId;
    private CallState _lastState = CallState.Idle;
    private bool _started;

    public IosCallIntegration(ICallService calls, ILogger<IosCallIntegration> logger)
    {
        _calls = calls;
        _logger = logger;
#pragma warning disable CA1422 // The installed .NET iOS binding requires the named constructor.
        var configuration = new CXProviderConfiguration("Aqorin Phone")
#pragma warning restore CA1422
        {
            SupportsVideo = false,
            MaximumCallGroups = 1,
            MaximumCallsPerCallGroup = 1,
            IncludesCallsInRecents = true,
        };
        _provider = new CXProvider(configuration);
        _delegate = new ProviderDelegate(this);
        _provider.SetDelegate(_delegate, DispatchQueue.MainQueue);
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _calls.CallChanged += OnCallChanged;
        HandleCall(_calls.CurrentCall);
    }

    public ValueTask DisposeAsync()
    {
        if (!_started)
        {
            return ValueTask.CompletedTask;
        }

        _started = false;
        _calls.CallChanged -= OnCallChanged;
        _provider.Invalidate();
        _provider.Dispose();
        _delegate.Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnCallChanged(object? sender, CallInfo call) => HandleCall(call);

    private void HandleCall(CallInfo call)
    {
        lock (_gate)
        {
            if (call.IsInProgress && (_nativeCallId is null || !string.Equals(_sipCallId, call.CallId, StringComparison.Ordinal)))
            {
                _nativeCallId?.Dispose();
                _nativeCallId = new NSUuid(Guid.NewGuid().ToString());
                _sipCallId = call.CallId;

                if (call.Direction == CallDirection.Incoming)
                {
                    var update = CreateUpdate(call);
                    _provider.ReportNewIncomingCall(_nativeCallId, update, error =>
                    {
                        if (error is not null)
                        {
                            _logger.LogWarning("CallKit rejected an incoming call: {Error}", error.LocalizedDescription);
                        }
                    });
                    update.Dispose();
                }
                else
                {
                    _provider.ReportConnectingOutgoingCall(_nativeCallId, NSDate.Now);
                }
            }

            if (_nativeCallId is not null
                && call.Direction == CallDirection.Outgoing
                && call.State == CallState.Active
                && _lastState != CallState.Active)
            {
                _provider.ReportConnectedOutgoingCall(_nativeCallId, NSDate.Now);
            }

            if (_nativeCallId is not null && !call.IsInProgress && _lastState is not (CallState.Idle or CallState.Failed))
            {
                _provider.ReportCall(_nativeCallId, NSDate.Now, MapEndReason(call.EndReason));
                _nativeCallId.Dispose();
                _nativeCallId = null;
                _sipCallId = null;
            }

            _lastState = call.State;
        }
    }

    private static CXCallUpdate CreateUpdate(CallInfo call)
    {
        var handle = new CXHandle(CXHandleType.PhoneNumber, string.IsNullOrWhiteSpace(call.RemoteParty) ? "Unknown" : call.RemoteParty);
        return new CXCallUpdate
        {
            RemoteHandle = handle,
            LocalizedCallerName = string.IsNullOrWhiteSpace(call.RemoteParty) ? "Aqorin call" : call.RemoteParty,
            HasVideo = false,
            SupportsDtmf = false,
            SupportsGrouping = false,
            SupportsHolding = true,
            SupportsUngrouping = false,
        };
    }

    private static CXCallEndedReason MapEndReason(CallEndReason reason) => reason switch
    {
        CallEndReason.RemoteHangup or CallEndReason.RemoteCancel => CXCallEndedReason.RemoteEnded,
        CallEndReason.LocalReject or CallEndReason.Rejected or CallEndReason.Busy => CXCallEndedReason.DeclinedElsewhere,
        CallEndReason.Timeout => CXCallEndedReason.Unanswered,
        CallEndReason.LocalHangup or CallEndReason.LocalCancel => CXCallEndedReason.AnsweredElsewhere,
        _ => CXCallEndedReason.Failed,
    };

    private async Task AnswerFromCallKitAsync(CXAnswerCallAction action)
    {
        try
        {
            await _calls.AnswerAsync().ConfigureAwait(false);
            action.Fulfill();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Answering from CallKit failed.");
            action.Fail();
        }
    }

    private async Task EndFromCallKitAsync(CXEndCallAction action)
    {
        try
        {
            if (_calls.CurrentCall.State == CallState.Incoming)
            {
                await _calls.RejectAsync().ConfigureAwait(false);
            }
            else if (_calls.CurrentCall.IsInProgress)
            {
                await _calls.HangupAsync().ConfigureAwait(false);
            }

            action.Fulfill();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ending from CallKit failed.");
            action.Fail();
        }
    }

    private async Task SetHeldFromCallKitAsync(CXSetHeldCallAction action)
    {
        try
        {
            await _calls.SetHoldAsync(action.OnHold).ConfigureAwait(false);
            action.Fulfill();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Changing hold state from CallKit failed.");
            action.Fail();
        }
    }

    private async Task SetMutedFromCallKitAsync(CXSetMutedCallAction action)
    {
        try
        {
            await _calls.SetMutedAsync(action.Muted).ConfigureAwait(false);
            action.Fulfill();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Changing mute state from CallKit failed.");
            action.Fail();
        }
    }

    private sealed class ProviderDelegate(IosCallIntegration owner) : CXProviderDelegate
    {
        public override void PerformAnswerCallAction(CXProvider provider, CXAnswerCallAction action) =>
            _ = owner.AnswerFromCallKitAsync(action);

        public override void PerformEndCallAction(CXProvider provider, CXEndCallAction action) =>
            _ = owner.EndFromCallKitAsync(action);

        public override void PerformSetHeldCallAction(CXProvider provider, CXSetHeldCallAction action) =>
            _ = owner.SetHeldFromCallKitAsync(action);

        public override void PerformSetMutedCallAction(CXProvider provider, CXSetMutedCallAction action) =>
            _ = owner.SetMutedFromCallKitAsync(action);

        public override void DidActivateAudioSession(CXProvider provider, AVAudioSession audioSession)
        {
        }

        public override void DidDeactivateAudioSession(CXProvider provider, AVAudioSession audioSession)
        {
        }

        public override void DidReset(CXProvider provider)
        {
        }
    }
}
