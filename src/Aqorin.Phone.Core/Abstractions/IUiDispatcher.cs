namespace Aqorin.Phone.Core.Abstractions;

/// <summary>Marshals service events (raised on SIP/audio threads) onto the UI thread.</summary>
public interface IUiDispatcher
{
    bool CheckAccess();

    void Post(Action action);

    Task InvokeAsync(Action action);
}

/// <summary>Runs everything inline. Used by unit tests.</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
