using Aqorin.Phone.Core.Model;

namespace Aqorin.Phone.Core.StateMachines;

/// <summary>
/// Pure, thread-agnostic transition table for <see cref="CallState"/>. Services own a lock around it.
/// </summary>
public static class CallStateMachine
{
    private static readonly Dictionary<CallState, CallState[]> Allowed = new()
    {
        [CallState.Idle] = [CallState.Dialing, CallState.Incoming],
        [CallState.Failed] = [CallState.Dialing, CallState.Incoming, CallState.Idle],
        [CallState.Dialing] = [CallState.Ringing, CallState.Connecting, CallState.Ending, CallState.Failed, CallState.Idle],
        [CallState.Ringing] = [CallState.Connecting, CallState.Ending, CallState.Failed, CallState.Idle],
        [CallState.Incoming] = [CallState.Connecting, CallState.Ending, CallState.Idle, CallState.Failed],
        [CallState.Connecting] = [CallState.Active, CallState.Ending, CallState.Failed, CallState.Idle],
        [CallState.Active] = [CallState.Ending, CallState.Idle, CallState.Failed],
        [CallState.Ending] = [CallState.Idle, CallState.Failed],
    };

    public static bool CanTransition(CallState from, CallState to) =>
        Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static void EnsureTransition(CallState from, CallState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Invalid call state transition {from} -> {to}.");
        }
    }

    /// <summary>A new outgoing call may be placed only from a resting state.</summary>
    public static bool CanPlaceCall(CallState state) => state is CallState.Idle or CallState.Failed;

    /// <summary>A new incoming call can be accepted for ringing only from a resting state; otherwise it must be answered busy.</summary>
    public static bool CanReceiveCall(CallState state) => state is CallState.Idle or CallState.Failed;

    public static bool CanAnswer(CallState state) => state == CallState.Incoming;

    public static bool CanReject(CallState state) => state == CallState.Incoming;

    public static bool CanHangup(CallState state) => state is CallState.Dialing or CallState.Ringing or CallState.Connecting or CallState.Active;
}
