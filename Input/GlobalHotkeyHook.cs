using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

internal sealed class GlobalHotkeyHook
{
    private const int LeftControlDown = 1 << 0;
    private const int RightControlDown = 1 << 1;
    private const int GenericControlDown = 1 << 2;
    private const int LeftShiftDown = 1 << 3;
    private const int RightShiftDown = 1 << 4;
    private const int GenericShiftDown = 1 << 5;
    private const int LeftAltDown = 1 << 6;
    private const int RightAltDown = 1 << 7;
    private const int GenericAltDown = 1 << 8;

    private readonly Func<bool> _canTrigger;
    private readonly NativeMethods.LowLevelKeyboardProcedure _keyboardProcedure;
    private readonly ShortcutTriggerGate _triggerGate = new();
    private Keys _shortcutKey;
    private Keys _shortcutModifiers;
    private volatile bool _capturingShortcut;
    private volatile bool _shortcutDown;
    private IntPtr _keyboardHook;
    private volatile int _modifierKeyState;

    internal GlobalHotkeyHook(Keys modifiers, Keys key, Func<bool> canTrigger)
    {
        _shortcutModifiers = modifiers;
        _shortcutKey = key;
        _canTrigger = canTrigger;
        _keyboardProcedure = KeyboardHookCallback;
    }

    internal event EventHandler? Pressed;
    internal event EventHandler? Released;

    internal bool Armed => _triggerGate.Armed;

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
            _modifierKeyState = 0;
        }
    }

    internal bool Install()
    {
        if (_keyboardHook == IntPtr.Zero)
        {
            _modifierKeyState = 0;
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
        _modifierKeyState = 0;
    }

    internal void Arm(IntPtr verifiedGameWindow)
    {
        _triggerGate.Arm(verifiedGameWindow);
    }

    internal void Disarm()
    {
        _triggerGate.Disarm();
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
            var message = wordParameter.ToInt32();
            var keyDown = message is
                NativeMethods.KeyDownMessage or NativeMethods.SystemKeyDownMessage;
            var keyUp = message is
                NativeMethods.KeyUpMessage or NativeMethods.SystemKeyUpMessage;
            _modifierKeyState = UpdateModifierKeyState(
                _modifierKeyState, (Keys)keyboardData.VirtualKeyCode, keyDown, keyUp);

            if (!_capturingShortcut && keyboardData.VirtualKeyCode == (uint)_shortcutKey)
            {
                var pressedModifiers =
                    GetPressedModifiers(_modifierKeyState, keyboardData.Flags);
                var modifiersMatch = pressedModifiers == _shortcutModifiers;

                var canTrigger = keyDown && modifiersMatch &&
                                 _triggerGate.CanFire(_canTrigger);
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

    internal static int UpdateModifierKeyState(
        int currentState, Keys key, bool keyDown, bool keyUp)
    {
        if (!keyDown && !keyUp)
        {
            return currentState;
        }

        var bit = key switch
        {
            Keys.LControlKey => LeftControlDown,
            Keys.RControlKey => RightControlDown,
            Keys.ControlKey => GenericControlDown,
            Keys.LShiftKey => LeftShiftDown,
            Keys.RShiftKey => RightShiftDown,
            Keys.ShiftKey => GenericShiftDown,
            Keys.LMenu => LeftAltDown,
            Keys.RMenu => RightAltDown,
            Keys.Menu => GenericAltDown,
            _ => 0
        };
        if (bit == 0)
        {
            return currentState;
        }

        return keyDown ? currentState | bit : currentState & ~bit;
    }

    internal static Keys GetPressedModifiers(int modifierKeyState, uint flags)
    {
        var modifiers = Keys.None;
        if ((modifierKeyState &
             (LeftControlDown | RightControlDown | GenericControlDown)) != 0)
        {
            modifiers |= Keys.Control;
        }
        if ((modifierKeyState &
             (LeftShiftDown | RightShiftDown | GenericShiftDown)) != 0)
        {
            modifiers |= Keys.Shift;
        }
        if ((modifierKeyState &
             (LeftAltDown | RightAltDown | GenericAltDown)) != 0 ||
            (flags & NativeMethods.AltDownFlag) != 0)
        {
            modifiers |= Keys.Alt;
        }
        return modifiers;
    }
}
