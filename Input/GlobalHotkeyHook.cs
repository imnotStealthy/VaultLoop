using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

internal sealed class GlobalHotkeyHook
{
    private readonly Func<bool> _canTrigger;
    private readonly NativeMethods.LowLevelKeyboardProcedure _keyboardProcedure;
    private Keys _shortcutKey;
    private Keys _shortcutModifiers;
    private volatile bool _capturingShortcut;
    private volatile bool _shortcutDown;
    private bool _gameHotkeyReady;
    private IntPtr _keyboardHook;
    private long _verifiedGameWindow;

    internal GlobalHotkeyHook(Keys modifiers, Keys key, Func<bool> canTrigger)
    {
        _shortcutModifiers = modifiers;
        _shortcutKey = key;
        _canTrigger = canTrigger;
        _keyboardProcedure = KeyboardHookCallback;
    }

    internal event EventHandler? Pressed;
    internal event EventHandler? Released;

    internal bool Armed => Volatile.Read(ref _gameHotkeyReady);

    internal bool CapturingShortcut
    {
        get => _capturingShortcut;
        set => _capturingShortcut = value;
    }

    internal (Keys Modifiers, Keys Key) Shortcut
    {
        get => (_shortcutModifiers, _shortcutKey);
        set
        {
            _shortcutModifiers = value.Modifiers;
            _shortcutKey = value.Key;
            _shortcutDown = false;
        }
    }

    internal bool Install()
    {
        if (_keyboardHook == IntPtr.Zero)
        {
            _keyboardHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.LowLevelKeyboardHook, _keyboardProcedure,
                NativeMethods.GetModuleHandle(null), 0);
        }
        return _keyboardHook != IntPtr.Zero;
    }

    internal void Uninstall()
    {
        if (_keyboardHook == IntPtr.Zero)
        {
            return;
        }
        NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = IntPtr.Zero;
    }

    internal void Arm(IntPtr verifiedGameWindow)
    {
        Interlocked.Exchange(ref _verifiedGameWindow, verifiedGameWindow.ToInt64());
        Volatile.Write(ref _gameHotkeyReady, true);
    }

    internal void Disarm()
    {
        Volatile.Write(ref _gameHotkeyReady, false);
        Interlocked.Exchange(ref _verifiedGameWindow, 0);
    }

    private IntPtr KeyboardHookCallback(
        int code, IntPtr wordParameter, IntPtr longParameter)
    {
        if (code >= 0)
        {
            var keyboardData =
                Marshal.PtrToStructure<NativeMethods.LowLevelKeyboardData>(longParameter);
            if ((keyboardData.Flags &
                 (NativeMethods.InjectedFlag | NativeMethods.LowerIntegrityInjectedFlag)) != 0)
            {
                return NativeMethods.CallNextHookEx(
                    _keyboardHook, code, wordParameter, longParameter);
            }
            if (!_capturingShortcut && keyboardData.VirtualKeyCode == (uint)_shortcutKey)
            {
                var message = wordParameter.ToInt32();
                var keyDown = message is
                    NativeMethods.KeyDownMessage or NativeMethods.SystemKeyDownMessage;
                var keyUp = message is
                    NativeMethods.KeyUpMessage or NativeMethods.SystemKeyUpMessage;
                var pressedModifiers = GetPressedModifiers(keyboardData.Flags);
                var modifiersMatch = pressedModifiers == _shortcutModifiers;

                var canTrigger = keyDown && modifiersMatch &&
                                 Volatile.Read(ref _gameHotkeyReady) &&
                                 GameProcessService.IsCurrentForegroundWindow(
                                     new IntPtr(
                                         Interlocked.Read(ref _verifiedGameWindow))) &&
                                 _canTrigger();
                if (canTrigger || (keyUp && _shortcutDown))
                {
                    if (keyDown && !_shortcutDown)
                    {
                        _shortcutDown = true;
                        Pressed?.Invoke(this, EventArgs.Empty);
                    }
                    else if (keyUp)
                    {
                        _shortcutDown = false;
                        Released?.Invoke(this, EventArgs.Empty);
                    }
                    return (IntPtr)1;
                }
            }
        }
        return NativeMethods.CallNextHookEx(
            _keyboardHook, code, wordParameter, longParameter);
    }

    private static Keys GetPressedModifiers(uint flags)
    {
        var modifiers = Keys.None;
        if ((flags & NativeMethods.AltDownFlag) != 0)
        {
            modifiers |= Keys.Alt;
        }
        if ((NativeMethods.GetAsyncKeyState((int)Keys.ControlKey) & 0x8000) != 0)
        {
            modifiers |= Keys.Control;
        }
        if ((NativeMethods.GetAsyncKeyState((int)Keys.ShiftKey) & 0x8000) != 0)
        {
            modifiers |= Keys.Shift;
        }
        return modifiers;
    }
}
