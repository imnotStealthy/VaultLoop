using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ReplayGlitchGTA;

internal sealed class ControllerShortcutService : IDisposable
{
    private const int PollIntervalMilliseconds = 30;
    internal const int HoldMilliseconds = 500;
    private const uint SonyVendorId = 0x054C;
    private const uint DualShock4FirstGeneration = 0x05C4;
    private const uint DualShock4SecondGeneration = 0x09CC;
    private const uint DualShock4WirelessAdapter = 0x0BA0;
    private const uint DualSense = 0x0CE6;
    private const uint DualSenseEdge = 0x0DF2;
    private const int MaximumRawInputBytes = 64 * 1024;

    private readonly object _sync = new();
    private readonly Func<bool> _canTrigger;
    private readonly ShortcutTriggerGate _triggerGate = new();
    private readonly Timer _pollTimer;
    private readonly Dictionary<string, DeviceState> _devices =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IntPtr, RawHidDevice> _rawDevices = new();

    /// <summary>Per device, the analog inputs currently treated as resting, not pressed.</summary>
    private readonly Dictionary<string, ControllerButtons> _stuckAnalogInputs =
        new(StringComparer.OrdinalIgnoreCase);

    private ControllerShortcut? _shortcut;
    private ControllerCaptureSnapshot _captureSnapshot =
        new("NOT CONFIGURED", null, complete: false);
    private string? _captureDeviceId;
    private ControllerButtons _captureCandidate;
    private long _captureHoldStarted;
    private bool _captureAwaitingRelease;
    private ControllerButtons _runtimeCandidate;
    private long _runtimeHoldStarted;
    private bool _runtimeLatched;
    private bool _runtimeRefusalReported;
    private bool _capturing;
    private bool _installed;
    private bool _rawInputRegistered;
    private bool _xinputAvailable = true;
    private int _polling;
    private bool _disposed;

    internal ControllerShortcutService(
        ControllerShortcut? shortcut, Func<bool> canTrigger)
    {
        _shortcut = shortcut;
        _canTrigger = canTrigger ?? throw new ArgumentNullException(nameof(canTrigger));
        _pollTimer = new Timer(PollControllers, null,
            Timeout.Infinite, Timeout.Infinite);
    }

    internal event EventHandler? Pressed;

    /// <summary>
    /// Raised when the configured combination was held for its full duration and the gate
    /// refused it. Without it a refused hold is indistinguishable from buttons that were never
    /// read at all, which is the whole difficulty of diagnosing a controller shortcut.
    /// </summary>
    internal event EventHandler? Refused;

    internal ControllerShortcut? Shortcut
    {
        get
        {
            lock (_sync)
            {
                return _shortcut;
            }
        }
        set
        {
            lock (_sync)
            {
                _shortcut = value;
                ResetRuntimeState();
            }
            UpdatePollTimer();
        }
    }

    internal ControllerCaptureSnapshot CaptureSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _captureSnapshot;
            }
        }
    }

    internal bool RawInputAvailable
    {
        get
        {
            lock (_sync)
            {
                return _rawInputRegistered;
            }
        }
    }

    internal bool Install(IntPtr windowHandle)
    {
        ThrowIfDisposed();
        var rawInputRegistered = RegisterRawInput(windowHandle, remove: false);
        lock (_sync)
        {
            _installed = true;
            _rawInputRegistered = rawInputRegistered;
        }
        UpdatePollTimer();
        return rawInputRegistered;
    }

    /// <summary>
    /// Starts or stops the poll timer. Polling has something to decide only while a capture is
    /// running or a shortcut is configured. Outside those two cases the timer used to keep
    /// waking the machine 33 times a second and sweeping four XInput slots for nothing —
    /// measured at 256 us per sweep, 0.85 % of one core, for a feature that is disabled by
    /// default.
    /// </summary>
    private void UpdatePollTimer()
    {
        bool pollingNeeded;
        lock (_sync)
        {
            pollingNeeded = _installed && (_capturing || _shortcut is not null);
        }
        _pollTimer.Change(
            pollingNeeded ? 0 : Timeout.Infinite,
            pollingNeeded ? PollIntervalMilliseconds : Timeout.Infinite);
    }

    internal void Uninstall()
    {
        _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
        bool unregisterRawInput;
        lock (_sync)
        {
            unregisterRawInput = _rawInputRegistered;
            _installed = false;
            _rawInputRegistered = false;
            _capturing = false;
            _captureDeviceId = null;
            _devices.Clear();
            _stuckAnalogInputs.Clear();
            foreach (var device in _rawDevices.Values)
            {
                device.Dispose();
            }
            _rawDevices.Clear();
            ResetRuntimeState();
        }
        if (unregisterRawInput)
        {
            RegisterRawInput(IntPtr.Zero, remove: true);
        }
        Disarm();
    }

    internal void Arm(IntPtr verifiedGameWindow)
    {
        _triggerGate.Arm(verifiedGameWindow);
    }

    internal void Suspend()
    {
        _triggerGate.Disarm();
    }

    internal void Disarm()
    {
        Suspend();
        lock (_sync)
        {
            ResetRuntimeTiming();
        }
    }

    internal void BeginCapture()
    {
        lock (_sync)
        {
            _capturing = true;
            _captureDeviceId = null;
            _captureCandidate = ControllerButtons.None;
            _captureHoldStarted = 0;
            _captureAwaitingRelease = false;
            _captureSnapshot = new ControllerCaptureSnapshot(
                _installed ? "PRESS A BUTTON ON YOUR CONTROLLER" :
                    "CONTROLLER INPUT UNAVAILABLE",
                null, complete: false);
            ResetRuntimeState();
        }
        UpdatePollTimer();
    }

    internal void CancelCapture()
    {
        lock (_sync)
        {
            _capturing = false;
            _captureDeviceId = null;
            _captureCandidate = ControllerButtons.None;
            _captureHoldStarted = 0;
            _captureAwaitingRelease = false;
        }
        UpdatePollTimer();
    }

    internal void ProcessRawInput(IntPtr rawInputHandle)
    {
        if (!_rawInputRegistered || rawInputHandle == IntPtr.Zero)
        {
            return;
        }

        var headerSize = (uint)Marshal.SizeOf<RawInputNativeMethods.RawInputHeader>();
        uint byteCount = 0;
        if (RawInputNativeMethods.GetRawInputData(
                rawInputHandle, RawInputNativeMethods.InputCommand,
                IntPtr.Zero, ref byteCount, headerSize) != 0 ||
            byteCount < headerSize || byteCount > MaximumRawInputBytes)
        {
            return;
        }

        var inputBuffer = Marshal.AllocHGlobal((int)byteCount);
        try
        {
            if (RawInputNativeMethods.GetRawInputData(
                    rawInputHandle, RawInputNativeMethods.InputCommand,
                    inputBuffer, ref byteCount, headerSize) != byteCount)
            {
                return;
            }

            var header = Marshal.PtrToStructure<RawInputNativeMethods.RawInputHeader>(
                inputBuffer);
            if (header.Type != RawInputNativeMethods.HidType ||
                !TryGetRawDevice(header.Device, out var device))
            {
                return;
            }

            var hidAddress = IntPtr.Add(inputBuffer, (int)headerSize);
            var rawHid = Marshal.PtrToStructure<RawInputNativeMethods.RawHid>(hidAddress);
            var reportDataAddress = IntPtr.Add(
                hidAddress, Marshal.SizeOf<RawInputNativeMethods.RawHid>());
            if (rawHid.SizeHid == 0 || rawHid.Count == 0 ||
                rawHid.SizeHid > MaximumRawInputBytes ||
                rawHid.Count > MaximumRawInputBytes / rawHid.SizeHid)
            {
                return;
            }

            var requiredBytes = (ulong)rawHid.SizeHid * rawHid.Count;
            var dataOffset = (ulong)headerSize +
                             (uint)Marshal.SizeOf<RawInputNativeMethods.RawHid>();
            if (dataOffset + requiredBytes > byteCount)
            {
                return;
            }

            var buttons = ControllerButtons.None;
            var report = new byte[rawHid.SizeHid];
            for (var reportIndex = 0u; reportIndex < rawHid.Count; reportIndex++)
            {
                Marshal.Copy(
                    IntPtr.Add(reportDataAddress, checked((int)(reportIndex * rawHid.SizeHid))),
                    report, 0, report.Length);
                buttons = device.ParseButtons(report);
            }
            UpdateDevice(device.DeviceId, device.DeviceKind, buttons);
        }
        finally
        {
            Marshal.FreeHGlobal(inputBuffer);
        }
    }

    internal void ProcessRawInputDeviceChange(IntPtr deviceHandle, int change)
    {
        if (change != RawInputNativeMethods.DeviceRemoval)
        {
            return;
        }

        RawHidDevice? removed = null;
        lock (_sync)
        {
            if (_rawDevices.TryGetValue(deviceHandle, out removed))
            {
                _rawDevices.Remove(deviceHandle);
                _devices.Remove(removed.DeviceId);
                _stuckAnalogInputs.Remove(removed.DeviceId);
                HandleDisconnectedDevice(removed.DeviceId);
            }
        }
        removed?.Dispose();
        UpdatePollTimer();
    }

    internal static ControllerButtons MapXInputButtons(
        XInputNativeMethods.XInputGamepad gamepad)
    {
        var buttons = ControllerButtons.None;
        AddXInputButton(ref buttons, gamepad.Buttons,
            XInputNativeMethods.DPadUp, ControllerButtons.DPadUp);
        AddXInputButton(ref buttons, gamepad.Buttons,
            XInputNativeMethods.DPadDown, ControllerButtons.DPadDown);
        AddXInputButton(ref buttons, gamepad.Buttons,
            XInputNativeMethods.DPadLeft, ControllerButtons.DPadLeft);
        AddXInputButton(ref buttons, gamepad.Buttons,
            XInputNativeMethods.DPadRight, ControllerButtons.DPadRight);
        AddXInputButton(ref buttons, gamepad.Buttons,
            XInputNativeMethods.Back, ControllerButtons.Back);
        AddXInputButton(ref buttons, gamepad.Buttons,
            XInputNativeMethods.Start, ControllerButtons.Start);
        AddXInputButton(ref buttons, gamepad.Buttons,
            XInputNativeMethods.LeftThumb, ControllerButtons.LeftStick);
        AddXInputButton(ref buttons, gamepad.Buttons,
            XInputNativeMethods.RightThumb, ControllerButtons.RightStick);
        AddXInputButton(ref buttons, gamepad.Buttons,
            XInputNativeMethods.LeftShoulder, ControllerButtons.LeftShoulder);
        AddXInputButton(ref buttons, gamepad.Buttons,
            XInputNativeMethods.RightShoulder, ControllerButtons.RightShoulder);
        AddXInputButton(ref buttons, gamepad.Buttons,
            XInputNativeMethods.A, ControllerButtons.South);
        AddXInputButton(ref buttons, gamepad.Buttons,
            XInputNativeMethods.B, ControllerButtons.East);
        AddXInputButton(ref buttons, gamepad.Buttons,
            XInputNativeMethods.X, ControllerButtons.West);
        AddXInputButton(ref buttons, gamepad.Buttons,
            XInputNativeMethods.Y, ControllerButtons.North);
        if (gamepad.LeftTrigger >= XInputNativeMethods.TriggerThreshold)
        {
            buttons |= ControllerButtons.LeftTrigger;
        }
        if (gamepad.RightTrigger >= XInputNativeMethods.TriggerThreshold)
        {
            buttons |= ControllerButtons.RightTrigger;
        }
        return buttons;
    }

    internal static ControllerDeviceKind? GetSonyDeviceKind(
        uint vendorId, uint productId)
    {
        if (vendorId != SonyVendorId)
        {
            return null;
        }
        return productId switch
        {
            DualShock4FirstGeneration or DualShock4SecondGeneration or
                DualShock4WirelessAdapter => ControllerDeviceKind.DualShock4,
            DualSense or DualSenseEdge => ControllerDeviceKind.DualSense,
            _ => null
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Uninstall();
        _pollTimer.Dispose();
        _disposed = true;
    }

    private void PollControllers(object? state)
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }

        try
        {
            // XInput is swept only when an Xbox device can actually take part: during capture,
            // or for a configured XInput shortcut. A DualShock or DualSense shortcut is fed by
            // raw input messages instead and needs no sweep at all.
            bool pollXInput;
            lock (_sync)
            {
                if (!_installed)
                {
                    return;
                }
                pollXInput = _capturing ||
                             _shortcut?.DeviceKind == ControllerDeviceKind.XInput;
            }
            if (pollXInput)
            {
                PollXInput();
            }

            RuntimeOutcome outcome;
            bool keepPolling;
            lock (_sync)
            {
                if (!_installed)
                {
                    return;
                }
                var timestamp = Stopwatch.GetTimestamp();
                if (_capturing)
                {
                    EvaluateCapture(timestamp);
                    outcome = RuntimeOutcome.None;
                }
                else
                {
                    outcome = EvaluateRuntime(timestamp);
                }
                keepPolling = _capturing || _shortcut is not null;
            }
            if (!keepPolling)
            {
                _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            if (outcome == RuntimeOutcome.Pressed)
            {
                Pressed?.Invoke(this, EventArgs.Empty);
            }
            else if (outcome == RuntimeOutcome.Refused)
            {
                Refused?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private void PollXInput()
    {
        if (!_xinputAvailable)
        {
            return;
        }

        for (var slot = 0; slot < 4; slot++)
        {
            uint result;
            XInputNativeMethods.XInputState state;
            try
            {
                result = XInputNativeMethods.XInputGetState((uint)slot, out state);
            }
            catch (DllNotFoundException)
            {
                _xinputAvailable = false;
                RemoveXInputDevices();
                return;
            }
            catch (EntryPointNotFoundException)
            {
                _xinputAvailable = false;
                RemoveXInputDevices();
                return;
            }

            var deviceId = $"xinput:{slot}";
            lock (_sync)
            {
                if (result == XInputNativeMethods.Success)
                {
                    _devices[deviceId] = new DeviceState(
                        deviceId, ControllerDeviceKind.XInput,
                        ApplyStuckAnalogFilter(deviceId, MapXInputButtons(state.Gamepad)));
                }
                else if (_devices.Remove(deviceId))
                {
                    _stuckAnalogInputs.Remove(deviceId);
                    HandleDisconnectedDevice(deviceId);
                }
            }
        }
    }

    private void EvaluateCapture(long timestamp)
    {
        if (_captureDeviceId is null)
        {
            foreach (var device in _devices.Values)
            {
                if (device.Buttons != ControllerButtons.None)
                {
                    _captureDeviceId = device.DeviceId;
                    _captureSnapshot = new ControllerCaptureSnapshot(
                        $"{device.DisplayName} DETECTED  //  ADD BUTTONS",
                        null, complete: false);
                    break;
                }
            }
        }

        if (_captureDeviceId is null ||
            !_devices.TryGetValue(_captureDeviceId, out var selectedDevice))
        {
            return;
        }

        var buttons = selectedDevice.Buttons;
        if (_captureAwaitingRelease)
        {
            _captureSnapshot = new ControllerCaptureSnapshot(
                "RELEASE ALL BUTTONS", null, complete: false);
            if (buttons != ControllerButtons.None)
            {
                return;
            }

            var captured = new ControllerShortcut(
                selectedDevice.DeviceKind, selectedDevice.DeviceId, _captureCandidate);
            _captureSnapshot = new ControllerCaptureSnapshot(
                captured.Format(), captured, complete: true);
            _capturing = false;
            return;
        }

        var buttonCount = ControllerShortcut.CountInputs(buttons);
        if (buttonCount == 0)
        {
            ResetCaptureCandidate();
            _captureSnapshot = new ControllerCaptureSnapshot(
                "PRESS 2 OR 3 BUTTONS", null, complete: false);
            return;
        }
        if (buttonCount == 1)
        {
            ResetCaptureCandidate();
            _captureSnapshot = new ControllerCaptureSnapshot(
                "ADD ANOTHER BUTTON", null, complete: false);
            return;
        }
        if (buttonCount > 3)
        {
            ResetCaptureCandidate();
            _captureSnapshot = new ControllerCaptureSnapshot(
                "USE ONLY 2 OR 3 BUTTONS", null, complete: false);
            return;
        }

        if (_captureCandidate != buttons)
        {
            _captureCandidate = buttons;
            _captureHoldStarted = timestamp;
        }

        var formatted = ControllerShortcut.FormatButtons(
            selectedDevice.DeviceKind, buttons);
        if (ElapsedMilliseconds(_captureHoldStarted, timestamp) < HoldMilliseconds)
        {
            _captureSnapshot = new ControllerCaptureSnapshot(
                $"{formatted}  //  HOLD", null, complete: false);
            return;
        }

        _captureAwaitingRelease = true;
        _captureSnapshot = new ControllerCaptureSnapshot(
            "RELEASE ALL BUTTONS", null, complete: false);
    }

    private RuntimeOutcome EvaluateRuntime(long timestamp)
    {
        if (_shortcut is null ||
            !_devices.TryGetValue(_shortcut.DeviceId, out var device) ||
            device.DeviceKind != _shortcut.DeviceKind)
        {
            ResetRuntimeState();
            return RuntimeOutcome.None;
        }

        if (_runtimeLatched)
        {
            if ((device.Buttons & _shortcut.Buttons) == ControllerButtons.None)
            {
                _runtimeLatched = false;
            }
            return RuntimeOutcome.None;
        }

        if (!ControllerShortcut.IsExactCombination(
                device.Buttons, _shortcut.Buttons))
        {
            ResetRuntimeTiming();
            return RuntimeOutcome.None;
        }

        if (_runtimeCandidate != device.Buttons)
        {
            _runtimeCandidate = device.Buttons;
            _runtimeHoldStarted = timestamp;
            return RuntimeOutcome.None;
        }
        if (ElapsedMilliseconds(_runtimeHoldStarted, timestamp) < HoldMilliseconds)
        {
            return RuntimeOutcome.None;
        }

        if (!_triggerGate.CanFire(_canTrigger))
        {
            // The hold restarts, so the shortcut fires as soon as the missing condition is
            // met without the user releasing anything. The refusal itself is reported once
            // per hold: at 30 ms per poll it would otherwise repeat 33 times a second.
            _runtimeHoldStarted = timestamp;
            if (_runtimeRefusalReported)
            {
                return RuntimeOutcome.None;
            }
            _runtimeRefusalReported = true;
            return RuntimeOutcome.Refused;
        }

        _runtimeLatched = true;
        ResetRuntimeTiming();
        return RuntimeOutcome.Pressed;
    }

    /// <summary>What one poll of a configured shortcut decided.</summary>
    private enum RuntimeOutcome
    {
        None,
        Pressed,
        Refused
    }

    private void UpdateDevice(
        string deviceId, ControllerDeviceKind deviceKind, ControllerButtons buttons)
    {
        lock (_sync)
        {
            _devices[deviceId] = new DeviceState(
                deviceId, deviceKind, ApplyStuckAnalogFilter(deviceId, buttons));
        }
    }

    /// <summary>
    /// Drops the analog inputs this device is resting on. A trigger is reported as a button
    /// once it passes its threshold, and a worn one can sit above it with nothing touching it —
    /// measured at 53 of 255 on the controller this was found with. Every combination read from
    /// that device then carries a trigger the user is not pressing, the exact match never holds,
    /// and the shortcut can never fire. A trigger stops being treated as resting the moment it
    /// is read below the threshold.
    /// </summary>
    private ControllerButtons ApplyStuckAnalogFilter(
        string deviceId, ControllerButtons buttons)
    {
        var previousStuck = _stuckAnalogInputs.TryGetValue(deviceId, out var known)
            ? known
            : (ControllerButtons?)null;
        var filtered = TrackStuckAnalogInputs(buttons, previousStuck, out var stuck);
        _stuckAnalogInputs[deviceId] = stuck;
        return filtered;
    }

    /// <summary>
    /// Reports the buttons of one reading with the resting analog inputs removed, and carries
    /// the resting set forward. <paramref name="previousStuck"/> is <c>null</c> for the first
    /// reading of a device, where anything already above its threshold is taken to be resting.
    /// </summary>
    internal static ControllerButtons TrackStuckAnalogInputs(
        ControllerButtons buttons, ControllerButtons? previousStuck,
        out ControllerButtons stuck)
    {
        var analog = buttons &
            (ControllerButtons.LeftTrigger | ControllerButtons.RightTrigger);
        stuck = previousStuck is null ? analog : previousStuck.Value & analog;
        return buttons & ~stuck;
    }

    private bool TryGetRawDevice(IntPtr deviceHandle, out RawHidDevice device)
    {
        lock (_sync)
        {
            if (_rawDevices.TryGetValue(deviceHandle, out device!))
            {
                return true;
            }
        }

        if (!RawHidDevice.TryCreate(deviceHandle, out var created))
        {
            device = null!;
            return false;
        }

        lock (_sync)
        {
            if (_rawDevices.TryGetValue(deviceHandle, out device!))
            {
                created.Dispose();
                return true;
            }
            _rawDevices.Add(deviceHandle, created);
            device = created;
            return true;
        }
    }

    private void HandleDisconnectedDevice(string deviceId)
    {
        if (string.Equals(_captureDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            _capturing = false;
            _captureSnapshot = new ControllerCaptureSnapshot(
                "CONTROLLER DISCONNECTED  //  RETRY", null,
                complete: false, retry: true);
            _captureDeviceId = null;
            ResetCaptureCandidate();
        }
        if (_shortcut is not null &&
            string.Equals(_shortcut.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            ResetRuntimeState();
        }
    }

    private void RemoveXInputDevices()
    {
        lock (_sync)
        {
            for (var slot = 0; slot < 4; slot++)
            {
                var deviceId = $"xinput:{slot}";
                if (_devices.Remove(deviceId))
                {
                    _stuckAnalogInputs.Remove(deviceId);
                    HandleDisconnectedDevice(deviceId);
                }
            }
        }
    }

    private void ResetCaptureCandidate()
    {
        _captureCandidate = ControllerButtons.None;
        _captureHoldStarted = 0;
        _captureAwaitingRelease = false;
    }

    private void ResetRuntimeState()
    {
        ResetRuntimeTiming();
        _runtimeLatched = false;
    }

    private void ResetRuntimeTiming()
    {
        _runtimeCandidate = ControllerButtons.None;
        _runtimeHoldStarted = 0;
        _runtimeRefusalReported = false;
    }

    private static long ElapsedMilliseconds(long started, long current) =>
        started == 0
            ? 0
            : (current - started) * 1000 / Stopwatch.Frequency;

    private static void AddXInputButton(
        ref ControllerButtons result, ushort value, ushort mask,
        ControllerButtons button)
    {
        if ((value & mask) != 0)
        {
            result |= button;
        }
    }

    private static bool RegisterRawInput(IntPtr windowHandle, bool remove)
    {
        var flags = remove
            ? RawInputNativeMethods.Remove
            : RawInputNativeMethods.InputSink | RawInputNativeMethods.DeviceNotify;
        var target = remove ? IntPtr.Zero : windowHandle;
        var devices = new[]
        {
            new RawInputNativeMethods.RawInputDevice
            {
                UsagePage = RawInputNativeMethods.GenericDesktopPage,
                Usage = RawInputNativeMethods.JoystickUsage,
                Flags = flags,
                TargetWindow = target
            },
            new RawInputNativeMethods.RawInputDevice
            {
                UsagePage = RawInputNativeMethods.GenericDesktopPage,
                Usage = RawInputNativeMethods.GamepadUsage,
                Flags = flags,
                TargetWindow = target
            }
        };
        return RawInputNativeMethods.RegisterRawInputDevices(
            devices, (uint)devices.Length,
            (uint)Marshal.SizeOf<RawInputNativeMethods.RawInputDevice>());
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ControllerShortcutService));
        }
    }

    private sealed class DeviceState
    {
        internal DeviceState(
            string deviceId, ControllerDeviceKind deviceKind, ControllerButtons buttons)
        {
            DeviceId = deviceId;
            DeviceKind = deviceKind;
            Buttons = buttons;
        }

        internal string DeviceId { get; }
        internal ControllerDeviceKind DeviceKind { get; }
        internal ControllerButtons Buttons { get; }
        internal string DisplayName =>
            ControllerShortcut.FormatDeviceName(DeviceKind, DeviceId);
    }

}
