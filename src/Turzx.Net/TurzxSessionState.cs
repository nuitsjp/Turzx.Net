namespace Turzx;

public enum TurzxSessionState { Created, Disconnected, Connecting, Ready, Faulted, Disposed }

public sealed class TurzxStateChangedEventArgs(TurzxSessionState oldState, TurzxSessionState newState, Exception? error) : EventArgs
{
    public TurzxSessionState OldState { get; } = oldState;
    public TurzxSessionState NewState { get; } = newState;
    public Exception? Error { get; } = error;
}

/// <summary>The frame may have reached the display, but its acknowledgement was not received.</summary>
public sealed class TurzxDeliveryUnknownException(string message, Exception innerException) : IOException(message, innerException);
