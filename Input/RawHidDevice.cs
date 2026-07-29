using System;
using System.Runtime.InteropServices;

namespace ReplayGlitchGTA;

internal sealed class RawHidDevice : IDisposable
{
    private const int MaximumHidUsages = 64;
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

        var deviceKind = ControllerShortcutService.GetSonyDeviceKind(
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
