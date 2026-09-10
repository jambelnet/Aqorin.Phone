namespace Aqorin.Phone.Core.Model;

/// <summary>Raised when an operation is not valid in the current call state (e.g. answering with no incoming call).</summary>
public sealed class InvalidCallOperationException : InvalidOperationException
{
    public InvalidCallOperationException(string operation, CallState state)
        : base($"Cannot {operation} while the call state is {state}.")
    {
        Operation = operation;
        State = state;
    }

    public string Operation { get; }
    public CallState State { get; }
}

/// <summary>Raised when an operation is not valid in the current registration state (e.g. registering twice).</summary>
public sealed class InvalidRegistrationOperationException : InvalidOperationException
{
    public InvalidRegistrationOperationException(string operation, RegistrationState state)
        : base($"Cannot {operation} while the registration state is {state}.")
    {
        Operation = operation;
        State = state;
    }

    public string Operation { get; }
    public RegistrationState State { get; }
}

/// <summary>Raised when a dialled destination cannot be turned into a SIP destination.</summary>
public sealed class InvalidDestinationException(string message) : ArgumentException(message);
