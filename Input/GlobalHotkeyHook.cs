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

    /// <summary>The modifier keys the hook tracks, and the bit each one owns.</summary>
    private static readonly (Keys Key, int Bit)[] ModifierKeys =
    [
        (Keys.LControlKey, LeftControlDown),
        (Keys.RControlKey, RightControlDown),
        (Keys.ControlKey, GenericControlDown),
        (Keys.LShiftKey, LeftShiftDown),
        (Keys.RShiftKey, RightShiftDown),
        (Keys.ShiftKey, GenericShiftDown),
        (Keys.LMenu, LeftAltDown),
        (Keys.RMenu, RightAltDown),
        (Keys.Menu, GenericAltDown)
    ];

    private readonly Func<bool> _canTrigger;
    private readonly NativeMethods.LowLevelKeyboardProcedure _keyboardProcedure;
    private readonly ShortcutTriggerGate _triggerGate = new();

    // The shortcut is written on the UI thread and read on the hook thread. Keys carries its
    // modifiers and its key code in disjoint bit ranges, so holding both in one volatile int
    // publishes them together: two separate fields could be read as a combination the user
    // never configured, which then matches nothing.
    private volatile int _shortcut;

    private volatile bool _capturingShortcut;
    private volatile bool _shortcutDown;

    // Set when a refused press has been reported, so auto-repeat reports it once. It is kept
    // apart from _shortcutDown on purpose: a refused key is passed on to the foreground
    // window, and swallowing its release would leave that window holding a key that never
    // comes back up.
    private volatile bool _refusalReported;

    private IntPtr _keyboardHook;
    private volatile int _modifierKeyState;

    internal GlobalHotkeyHook(Keys modifiers, Keys key, Func<bool> canTrigger)
    {
        _shortcut = PackShortcut(modifiers, key);
        _canTrigger = canTrigger;
        _keyboardProcedure = KeyboardHookCallback;
    }

    internal event EventHandler? Pressed;
    internal event EventHandler? Released;

    /// <summary>
    /// Raised when the configured shortcut was pressed with its modifiers and the gate refused
    /// it. A refusal is a normal outcome — no administrator rights, no verified game in the
    /// foreground — but it used to be indistinguishable from a broken keyboard, so the window
    /// says which condition is missing instead of doing nothing at all.
    /// </summary>
    internal event EventHandler? Refused;

    internal bool Armed => _triggerGate.Armed;

    internal bool CapturingShortcut
    {
        get => _capturingShortcut;
        set => _capturingShortcut = value;
    }

    internal (Keys Modifiers, Keys Key) Shortcut
    {
        get
        {
            var shortcut = (Keys)_shortcut;
            return (shortcut & Keys.Modifiers, shortcut & Keys.KeyCode);
        }
        set
        {
            _shortcut = PackShortcut(value.Modifiers, value.Key);
            _shortcutDown = false;
            _refusalReported = false;
            _modifierKeyState = 0;
        }
    }

    internal bool Install()
    {
        if (_keyboardHook == IntPtr.Zero)
        {
            _modifierKeyState = 0;
            _shortcutDown = false;
            _refusalReported = false;
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
        _shortcutDown = false;
        _refusalReported = false;
    }

    internal void Arm(IntPtr verifiedGameWindow)
    {
        _triggerGate.Arm(verifiedGameWindow);
    }

    internal void Disarm()
    {
        // Disarming is the one moment where the hook is known to be out of the picture, so it
        // doubles as the resynchronization point for the held-key latch. A release that the
        // hook never observes — the secure desktop swallows every key event while a UAC prompt
        // or Ctrl+Alt+Del is up — would otherwise leave the latch set for the rest of the
        // session, and every later press was swallowed without toggling anything.
        _shortcutDown = false;
        _triggerGate.Disarm();
    }

    private IntPtr KeyboardHookCallback(
        int code, IntPtr wordParameter, IntPtr longParameter)
    {
        if (code < 0)
        {
            return NativeMethods.CallNextHookEx(
                _keyboardHook, code, wordParameter, longParameter);
        }

        var keyboardData =
            Marshal.PtrToStructure<NativeMethods.LowLevelKeyboardData>(longParameter);
        var message = wordParameter.ToInt32();
        var keyDown = message is
            NativeMethods.KeyDownMessage or NativeMethods.SystemKeyDownMessage;
        var keyUp = message is
            NativeMethods.KeyUpMessage or NativeMethods.SystemKeyUpMessage;

        var injected = IsInjectedEvent(keyboardData.Flags);
        // Reject injected events before they can affect the tracked keyboard state or the
        // held-key latch. ReconcileModifierKeyState below repairs transitions the hook genuinely
        // missed, without allowing synthetic input to influence a shortcut.
        if (injected)
        {
            return NativeMethods.CallNextHookEx(
                _keyboardHook, code, wordParameter, longParameter);
        }

        _modifierKeyState = UpdateModifierKeyState(
            _modifierKeyState, (Keys)keyboardData.VirtualKeyCode, keyDown, keyUp);

        var shortcut = (Keys)_shortcut;
        if (keyboardData.VirtualKeyCode == (uint)(shortcut & Keys.KeyCode) &&
            HandleShortcutKey(shortcut, keyboardData.Flags, keyDown, keyUp, injected))
        {
            return (IntPtr)1;
        }
        return NativeMethods.CallNextHookEx(
            _keyboardHook, code, wordParameter, longParameter);
    }

    /// <summary>
    /// Handles one event for the configured shortcut key and reports whether it was consumed.
    /// Injected events are never consumed, including a key release, so synthetic input cannot
    /// clear the latch. A release the hook genuinely misses costs the user the next real press,
    /// which is why Disarm and ReconcileModifierKeyState resynchronize the state.
    /// </summary>
    private bool HandleShortcutKey(
        Keys shortcut, uint flags, bool keyDown, bool keyUp, bool injected)
    {
        if (injected)
        {
            return false;
        }

        if (keyUp)
        {
            _refusalReported = false;
            if (!_shortcutDown)
            {
                return false;
            }
            _shortcutDown = false;
            Released?.Invoke(this, EventArgs.Empty);
            return true;
        }

        if (!keyDown || _capturingShortcut)
        {
            return false;
        }

        _modifierKeyState = ReconcileModifierKeyState(_modifierKeyState, IsKeyPhysicallyDown);
        if (GetPressedModifiers(_modifierKeyState, flags) != (shortcut & Keys.Modifiers))
        {
            return false;
        }

        if (!_triggerGate.CanFire(_canTrigger))
        {
            if (!_refusalReported)
            {
                _refusalReported = true;
                Refused?.Invoke(this, EventArgs.Empty);
            }
            return false;
        }

        // Auto-repeat while the key is held is consumed without firing again: one press is one
        // toggle.
        if (!_shortcutDown)
        {
            _shortcutDown = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        return true;
    }

    internal static int UpdateModifierKeyState(
        int currentState, Keys key, bool keyDown, bool keyUp)
    {
        if (!keyDown && !keyUp)
        {
            return currentState;
        }

        var bit = GetModifierBit(key);
        if (bit == 0)
        {
            return currentState;
        }

        return keyDown ? currentState | bit : currentState & ~bit;
    }

    internal static bool IsInjectedEvent(uint flags) =>
        (flags & (NativeMethods.InjectedFlag | NativeMethods.LowerIntegrityInjectedFlag)) != 0;

    /// <summary>
    /// Rebuilds the tracked modifier state from the live keyboard. The hook only ever sees the
    /// events Windows delivers to it, and a transition made while it could not observe one —
    /// the secure desktop, a session switch, a remote session taking over — leaves the tracked
    /// state describing a keyboard that no longer exists. The shortcut then matches nothing
    /// until VaultLoop is restarted, which reads as the shortcut having simply stopped working.
    /// </summary>
    internal static int ReconcileModifierKeyState(int currentState, Func<Keys, bool> isKeyDown)
    {
        var state = currentState;
        foreach (var (key, bit) in ModifierKeys)
        {
            state = isKeyDown(key) ? state | bit : state & ~bit;
        }
        return state;
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

    private static bool IsKeyPhysicallyDown(Keys key) =>
        (NativeMethods.GetAsyncKeyState((int)key) & 0x8000) != 0;

    private static int GetModifierBit(Keys key)
    {
        foreach (var (modifierKey, bit) in ModifierKeys)
        {
            if (modifierKey == key)
            {
                return bit;
            }
        }
        return 0;
    }

    private static int PackShortcut(Keys modifiers, Keys key) =>
        (int)((modifiers & Keys.Modifiers) | (key & Keys.KeyCode));
}
