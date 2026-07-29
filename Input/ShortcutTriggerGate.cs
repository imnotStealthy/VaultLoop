using System;
using System.Threading;

namespace ReplayGlitchGTA;

internal sealed class ShortcutTriggerGate
{
    private volatile bool _gameHotkeyReady;
    private long _verifiedGameWindow;

    internal void Arm(IntPtr verifiedGameWindow)
    {
        Volatile.Write(ref _verifiedGameWindow, verifiedGameWindow.ToInt64());
        _gameHotkeyReady = verifiedGameWindow != IntPtr.Zero;
    }

    internal void Disarm()
    {
        _gameHotkeyReady = false;
        Volatile.Write(ref _verifiedGameWindow, 0);
    }

    internal bool Armed => _gameHotkeyReady;

    internal bool CanFire(Func<bool> canTrigger)
    {
        if (!_gameHotkeyReady)
        {
            return false;
        }

        var verifiedGameWindow = new IntPtr(Volatile.Read(ref _verifiedGameWindow));
        if (verifiedGameWindow == IntPtr.Zero)
        {
            return false;
        }
        if (!GameProcessService.IsCurrentForegroundWindow(verifiedGameWindow))
        {
            return false;
        }
        return canTrigger();
    }
}
