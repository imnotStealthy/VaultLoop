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
    private const int MaximumHidUsages = 64;

    private readonly object _sync = new();
    private readonly Func<bool> _canTrigger;
    private readonly Timer _pollTimer;
    private readonly Dictionary<string, DeviceState> _devices =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IntPtr, RawHidDevice> _rawDevices = new();

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
    private bool _capturing;
    private bool _installed;
    private bool _rawInputRegistered;
    private bool _xinputAvailable = true;
    private int _polling;
    private volatile bool _gameHotkeyReady;
    private long _verifiedGameWindow;
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
        _pollTimer.Change(0, PollIntervalMilliseconds);
        return rawInputRegistered;
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
        Volatile.Write(ref _verifiedGameWindow, verifiedGameWindow.ToInt64());
        _gameHotkeyReady = verifiedGameWindow != IntPtr.Zero;
    }

    internal void Suspend()
    {
        _gameHotkeyReady = false;
        Volatile.Write(ref _verifiedGameWindow, 0);
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
                HandleDisconnectedDevice(removed.DeviceId);
            }
        }
        removed?.Dispose();
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
            PollXInput();
            bool raisePressed;
            lock (_sync)
            {
                if (!_installed)
                {
                    return;
                }
                var timestamp = Stopwatch.GetTimestamp();
                raisePressed = _capturing
                    ? EvaluateCapture(timestamp)
                    : EvaluateRuntime(timestamp);
            }
            if (raisePressed)
            {
                Pressed?.Invoke(this, EventArgs.Empty);
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
                        MapXInputButtons(state.Gamepad));
                }
                else if (_devices.Remove(deviceId))
                {
                    HandleDisconnectedDevice(deviceId);
                }
            }
        }
    }

    private bool EvaluateCapture(long timestamp)
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
            return false;
        }

        var buttons = selectedDevice.Buttons;
        if (_captureAwaitingRelease)
        {
            _captureSnapshot = new ControllerCaptureSnapshot(
                "RELEASE ALL BUTTONS", null, complete: false);
            if (buttons != ControllerButtons.None)
            {
                return false;
            }

            var captured = new ControllerShortcut(
                selectedDevice.DeviceKind, selectedDevice.DeviceId, _captureCandidate);
            _captureSnapshot = new ControllerCaptureSnapshot(
                captured.Format(), captured, complete: true);
            _capturing = false;
            return false;
        }

        var buttonCount = ControllerShortcut.CountInputs(buttons);
        if (buttonCount == 0)
        {
            ResetCaptureCandidate();
            _captureSnapshot = new ControllerCaptureSnapshot(
                "PRESS 2 OR 3 BUTTONS", null, complete: false);
            return false;
        }
        if (buttonCount == 1)
        {
            ResetCaptureCandidate();
            _captureSnapshot = new ControllerCaptureSnapshot(
                "ADD ANOTHER BUTTON", null, complete: false);
            return false;
        }
        if (buttonCount > 3)
        {
            ResetCaptureCandidate();
            _captureSnapshot = new ControllerCaptureSnapshot(
                "USE ONLY 2 OR 3 BUTTONS", null, complete: false);
            return false;
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
            return false;
        }

        _captureAwaitingRelease = true;
        _captureSnapshot = new ControllerCaptureSnapshot(
            "RELEASE ALL BUTTONS", null, complete: false);
        return false;
    }

    private bool EvaluateRuntime(long timestamp)
    {
        if (_shortcut is null ||
            !_devices.TryGetValue(_shortcut.DeviceId, out var device) ||
            device.DeviceKind != _shortcut.DeviceKind)
        {
            ResetRuntimeState();
            return false;
        }

        if (_runtimeLatched)
        {
            if ((device.Buttons & _shortcut.Buttons) == ControllerButtons.None)
            {
                _runtimeLatched = false;
            }
            return false;
        }

        if (!ControllerShortcut.IsExactCombination(
                device.Buttons, _shortcut.Buttons))
        {
            _runtimeCandidate = ControllerButtons.None;
            _runtimeHoldStarted = 0;
            return false;
        }

        if (_runtimeCandidate != device.Buttons)
        {
            _runtimeCandidate = device.Buttons;
            _runtimeHoldStarted = timestamp;
            return false;
        }
        if (ElapsedMilliseconds(_runtimeHoldStarted, timestamp) < HoldMilliseconds)
        {
            return false;
        }

        var verifiedWindow = new IntPtr(Volatile.Read(ref _verifiedGameWindow));
        if (!_gameHotkeyReady || verifiedWindow == IntPtr.Zero)
        {
            return false;
        }
        if (!_canTrigger() ||
            !GameProcessService.IsCurrentForegroundWindow(verifiedWindow))
        {
            _runtimeHoldStarted = timestamp;
            return false;
        }

        _runtimeLatched = true;
        _runtimeCandidate = ControllerButtons.None;
        _runtimeHoldStarted = 0;
        return true;
    }

    private void UpdateDevice(
        string deviceId, ControllerDeviceKind deviceKind, ControllerButtons buttons)
    {
        lock (_sync)
        {
            _devices[deviceId] = new DeviceState(deviceId, deviceKind, buttons);
        }
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
        internal string DisplayName => DeviceKind switch
        {
            ControllerDeviceKind.XInput =>
                $"Xbox Controller {ControllerShortcut.ParseXInputSlot(DeviceId) + 1}",
            ControllerDeviceKind.DualShock4 => "DualShock 4",
            ControllerDeviceKind.DualSense => "DualSense",
            _ => "Controller"
        };
    }

    private sealed class RawHidDevice : IDisposable
    {
        private const int PreparsedDataMaximumBytes = 64 * 1024;
        private IntPtr _preparsedData;

        private RawHidDevice(
            string deviceId, ControllerDeviceKind deviceKind, IntPtr preparsedData)
        {
            DeviceId = deviceId;
            DeviceKind = deviceKind;
            _preparsedData = preparsedData;
        }

        internal string DeviceId { get; }
        internal ControllerDeviceKind DeviceKind { get; }

        internal ControllerButtons ParseButtons(byte[] report)
        {
            var usages = new RawInputNativeMethods.UsageAndPage[MaximumHidUsages];
            uint usageCount = (uint)usages.Length;
            var status = RawInputNativeMethods.HidP_GetUsagesEx(
                RawInputNativeMethods.HidpInputReport, 0, usages, ref usageCount,
                _preparsedData, report, (uint)report.Length);
            if (status != RawInputNativeMethods.HidpStatusSuccess)
            {
                return ControllerButtons.None;
            }

            var result = ControllerButtons.None;
            for (var index = 0; index < usageCount && index < usages.Length; index++)
            {
                if (usages[index].UsagePage == RawInputNativeMethods.ButtonPage)
                {
                    result |= MapSonyButtonUsage(usages[index].Usage);
                }
            }
            if (RawInputNativeMethods.HidP_GetUsageValue(
                    RawInputNativeMethods.HidpInputReport,
                    RawInputNativeMethods.GenericDesktopPage, 0,
                    RawInputNativeMethods.HatSwitchUsage, out var hatValue,
                    _preparsedData, report, (uint)report.Length) ==
                RawInputNativeMethods.HidpStatusSuccess)
            {
                result |= MapHatSwitch(hatValue);
            }
            return result;
        }

        internal static bool TryCreate(IntPtr deviceHandle, out RawHidDevice device)
        {
            device = null!;
            var info = new RawInputNativeMethods.RawInputDeviceInfo
            {
                Size = (uint)Marshal.SizeOf<RawInputNativeMethods.RawInputDeviceInfo>()
            };
            var infoSize = info.Size;
            if (RawInputNativeMethods.GetRawInputDeviceInfo(
                    deviceHandle, RawInputNativeMethods.DeviceInfoCommand,
                    ref info, ref infoSize) == RawInputNativeMethods.Error ||
                info.Type != RawInputNativeMethods.HidType)
            {
                return false;
            }

            var deviceKind = GetSonyDeviceKind(
                info.Device.Hid.VendorId, info.Device.Hid.ProductId);
            if (!deviceKind.HasValue ||
                !TryGetDeviceName(deviceHandle, out var deviceName) ||
                !TryGetPreparsedData(deviceHandle, out var preparsedData))
            {
                return false;
            }

            device = new RawHidDevice(deviceName, deviceKind.Value, preparsedData);
            return true;
        }

        public void Dispose()
        {
            if (_preparsedData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_preparsedData);
                _preparsedData = IntPtr.Zero;
            }
        }

        private static bool TryGetDeviceName(IntPtr deviceHandle, out string deviceName)
        {
            deviceName = "";
            uint characterCount = 0;
            if (RawInputNativeMethods.GetRawInputDeviceInfo(
                    deviceHandle, RawInputNativeMethods.DeviceNameCommand,
                    IntPtr.Zero, ref characterCount) == RawInputNativeMethods.Error ||
                characterCount == 0 || characterCount > 2048)
            {
                return false;
            }

            var nameBuffer = Marshal.AllocHGlobal(checked((int)((characterCount + 1) * 2)));
            try
            {
                if (RawInputNativeMethods.GetRawInputDeviceInfo(
                        deviceHandle, RawInputNativeMethods.DeviceNameCommand,
                        nameBuffer, ref characterCount) == RawInputNativeMethods.Error)
                {
                    return false;
                }
                deviceName = Marshal.PtrToStringUni(nameBuffer, (int)characterCount)?
                    .TrimEnd('\0') ?? "";
                return ControllerShortcut.IsValidDeviceId(
                    ControllerDeviceKind.DualSense, deviceName);
            }
            finally
            {
                Marshal.FreeHGlobal(nameBuffer);
            }
        }

        private static bool TryGetPreparsedData(
            IntPtr deviceHandle, out IntPtr preparsedData)
        {
            preparsedData = IntPtr.Zero;
            uint byteCount = 0;
            if (RawInputNativeMethods.GetRawInputDeviceInfo(
                    deviceHandle, RawInputNativeMethods.PreparsedDataCommand,
                    IntPtr.Zero, ref byteCount) == RawInputNativeMethods.Error ||
                byteCount == 0 || byteCount > PreparsedDataMaximumBytes)
            {
                return false;
            }

            var data = Marshal.AllocHGlobal((int)byteCount);
            if (RawInputNativeMethods.GetRawInputDeviceInfo(
                    deviceHandle, RawInputNativeMethods.PreparsedDataCommand,
                    data, ref byteCount) == RawInputNativeMethods.Error)
            {
                Marshal.FreeHGlobal(data);
                return false;
            }
            preparsedData = data;
            return true;
        }

        private static ControllerButtons MapSonyButtonUsage(ushort usage) =>
            usage switch
            {
                1 => ControllerButtons.West,
                2 => ControllerButtons.South,
                3 => ControllerButtons.East,
                4 => ControllerButtons.North,
                5 => ControllerButtons.LeftShoulder,
                6 => ControllerButtons.RightShoulder,
                7 => ControllerButtons.LeftTrigger,
                8 => ControllerButtons.RightTrigger,
                9 => ControllerButtons.Back,
                10 => ControllerButtons.Start,
                11 => ControllerButtons.LeftStick,
                12 => ControllerButtons.RightStick,
                13 => ControllerButtons.Guide,
                14 => ControllerButtons.Touchpad,
                _ => ControllerButtons.None
            };

        private static ControllerButtons MapHatSwitch(uint value) =>
            value switch
            {
                0 => ControllerButtons.DPadUp,
                1 => ControllerButtons.DPadUp | ControllerButtons.DPadRight,
                2 => ControllerButtons.DPadRight,
                3 => ControllerButtons.DPadDown | ControllerButtons.DPadRight,
                4 => ControllerButtons.DPadDown,
                5 => ControllerButtons.DPadDown | ControllerButtons.DPadLeft,
                6 => ControllerButtons.DPadLeft,
                7 => ControllerButtons.DPadUp | ControllerButtons.DPadLeft,
                _ => ControllerButtons.None
            };
    }
}
