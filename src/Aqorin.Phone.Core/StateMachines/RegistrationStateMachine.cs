using Aqorin.Phone.Core.Model;

namespace Aqorin.Phone.Core.StateMachines;

/// <summary>Pure transition table for <see cref="RegistrationState"/>.</summary>
public static class RegistrationStateMachine
{
    private static readonly Dictionary<RegistrationState, RegistrationState[]> Allowed = new()
    {
        [RegistrationState.Disconnected] = [RegistrationState.Registering],
        [RegistrationState.Failed] = [RegistrationState.Registering, RegistrationState.Disconnected],
        [RegistrationState.Registering] = [RegistrationState.Registered, RegistrationState.Failed, RegistrationState.Disconnected, RegistrationState.Unregistering],
        [RegistrationState.Registered] = [RegistrationState.Registering, RegistrationState.Unregistering, RegistrationState.Failed, RegistrationState.Disconnected],
        [RegistrationState.Unregistering] = [RegistrationState.Disconnected, RegistrationState.Failed],
    };

    public static bool CanTransition(RegistrationState from, RegistrationState to) =>
        Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static void EnsureTransition(RegistrationState from, RegistrationState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Invalid registration state transition {from} -> {to}.");
        }
    }

    /// <summary>Register is allowed only when nothing is registered or in flight.</summary>
    public static bool CanRegister(RegistrationState state) => state is RegistrationState.Disconnected or RegistrationState.Failed;

    /// <summary>Unregister is allowed while registered or while an attempt/retry is pending.</summary>
    public static bool CanUnregister(RegistrationState state) => state is RegistrationState.Registered or RegistrationState.Registering or RegistrationState.Failed;
}
