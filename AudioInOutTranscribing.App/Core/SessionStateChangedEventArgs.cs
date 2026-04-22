namespace AudioInOutTranscribing.App.Core;

public sealed class SessionStateChangedEventArgs : EventArgs
{
    public SessionStateChangedEventArgs(TrayState state, string message)
    {
        State = state;
        Message = message;
    }

    public TrayState State { get; }

    public string Message { get; }
}
