using Aqorin.Phone.Core.Model;
using Aqorin.Phone.Core.StateMachines;

namespace Aqorin.Phone.Core.Tests;

public class CallStateMachineTests
{
    [Theory]
    [InlineData(CallState.Idle, CallState.Dialing)]
    [InlineData(CallState.Idle, CallState.Incoming)]
    [InlineData(CallState.Failed, CallState.Dialing)]
    [InlineData(CallState.Dialing, CallState.Ringing)]
    [InlineData(CallState.Dialing, CallState.Connecting)]
    [InlineData(CallState.Dialing, CallState.Failed)]
    [InlineData(CallState.Ringing, CallState.Connecting)]
    [InlineData(CallState.Ringing, CallState.Ending)]
    [InlineData(CallState.Incoming, CallState.Connecting)]
    [InlineData(CallState.Incoming, CallState.Idle)]
    [InlineData(CallState.Connecting, CallState.Active)]
    [InlineData(CallState.Active, CallState.Ending)]
    [InlineData(CallState.Active, CallState.Idle)]
    [InlineData(CallState.Ending, CallState.Idle)]
    public void Valid_transitions_are_allowed(CallState from, CallState to)
    {
        Assert.True(CallStateMachine.CanTransition(from, to));
        CallStateMachine.EnsureTransition(from, to);
    }

    [Theory]
    [InlineData(CallState.Idle, CallState.Active)]
    [InlineData(CallState.Idle, CallState.Ringing)]
    [InlineData(CallState.Idle, CallState.Ending)]
    [InlineData(CallState.Incoming, CallState.Ringing)]
    [InlineData(CallState.Active, CallState.Dialing)]
    [InlineData(CallState.Active, CallState.Incoming)]
    [InlineData(CallState.Ending, CallState.Active)]
    [InlineData(CallState.Dialing, CallState.Incoming)]
    public void Invalid_transitions_are_rejected(CallState from, CallState to)
    {
        Assert.False(CallStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(() => CallStateMachine.EnsureTransition(from, to));
    }

    [Fact]
    public void Guards_reflect_single_call_rules()
    {
        Assert.True(CallStateMachine.CanPlaceCall(CallState.Idle));
        Assert.True(CallStateMachine.CanPlaceCall(CallState.Failed));
        Assert.False(CallStateMachine.CanPlaceCall(CallState.Active));
        Assert.False(CallStateMachine.CanPlaceCall(CallState.Dialing));
        Assert.False(CallStateMachine.CanPlaceCall(CallState.Incoming));

        Assert.True(CallStateMachine.CanAnswer(CallState.Incoming));
        Assert.False(CallStateMachine.CanAnswer(CallState.Idle));
        Assert.False(CallStateMachine.CanAnswer(CallState.Active));

        Assert.True(CallStateMachine.CanReject(CallState.Incoming));
        Assert.False(CallStateMachine.CanReject(CallState.Ringing));

        Assert.True(CallStateMachine.CanHangup(CallState.Dialing));
        Assert.True(CallStateMachine.CanHangup(CallState.Ringing));
        Assert.True(CallStateMachine.CanHangup(CallState.Connecting));
        Assert.True(CallStateMachine.CanHangup(CallState.Active));
        Assert.False(CallStateMachine.CanHangup(CallState.Idle));
        Assert.False(CallStateMachine.CanHangup(CallState.Incoming));
        Assert.False(CallStateMachine.CanHangup(CallState.Ending));
        Assert.False(CallStateMachine.CanHangup(CallState.Failed));
    }
}

public class RegistrationStateMachineTests
{
    [Theory]
    [InlineData(RegistrationState.Disconnected, RegistrationState.Registering)]
    [InlineData(RegistrationState.Registering, RegistrationState.Registered)]
    [InlineData(RegistrationState.Registering, RegistrationState.Failed)]
    [InlineData(RegistrationState.Registered, RegistrationState.Unregistering)]
    [InlineData(RegistrationState.Registered, RegistrationState.Registering)]
    [InlineData(RegistrationState.Unregistering, RegistrationState.Disconnected)]
    [InlineData(RegistrationState.Failed, RegistrationState.Registering)]
    public void Valid_transitions(RegistrationState from, RegistrationState to)
    {
        Assert.True(RegistrationStateMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData(RegistrationState.Disconnected, RegistrationState.Registered)]
    [InlineData(RegistrationState.Disconnected, RegistrationState.Unregistering)]
    [InlineData(RegistrationState.Unregistering, RegistrationState.Registered)]
    [InlineData(RegistrationState.Failed, RegistrationState.Registered)]
    public void Invalid_transitions(RegistrationState from, RegistrationState to)
    {
        Assert.False(RegistrationStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(() => RegistrationStateMachine.EnsureTransition(from, to));
    }

    [Fact]
    public void Registering_twice_is_not_allowed()
    {
        Assert.True(RegistrationStateMachine.CanRegister(RegistrationState.Disconnected));
        Assert.True(RegistrationStateMachine.CanRegister(RegistrationState.Failed));
        Assert.False(RegistrationStateMachine.CanRegister(RegistrationState.Registering));
        Assert.False(RegistrationStateMachine.CanRegister(RegistrationState.Registered));
        Assert.False(RegistrationStateMachine.CanRegister(RegistrationState.Unregistering));

        Assert.True(RegistrationStateMachine.CanUnregister(RegistrationState.Registered));
        Assert.True(RegistrationStateMachine.CanUnregister(RegistrationState.Registering));
        Assert.False(RegistrationStateMachine.CanUnregister(RegistrationState.Disconnected));
        Assert.False(RegistrationStateMachine.CanUnregister(RegistrationState.Unregistering));
    }
}
