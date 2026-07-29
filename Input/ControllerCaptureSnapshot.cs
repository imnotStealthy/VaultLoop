namespace ReplayGlitchGTA;

internal sealed class ControllerCaptureSnapshot
{
    internal ControllerCaptureSnapshot(
        string statusText, ControllerShortcut? shortcut, bool complete,
        bool retry = false)
    {
        StatusText = statusText;
        Shortcut = shortcut;
        Complete = complete;
        Retry = retry;
    }

    internal string StatusText { get; }
    internal ControllerShortcut? Shortcut { get; }
    internal bool Complete { get; }
    internal bool Retry { get; }
}
