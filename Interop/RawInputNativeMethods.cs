using System;
using System.Runtime.InteropServices;

namespace ReplayGlitchGTA;

internal static class RawInputNativeMethods
{
    internal const int InputMessage = 0x00FF;
    internal const int InputDeviceChangeMessage = 0x00FE;
    internal const int DeviceRemoval = 2;

    internal const uint InputSink = 0x00000100;
    internal const uint DeviceNotify = 0x00002000;
    internal const uint Remove = 0x00000001;
    internal const ushort GenericDesktopPage = 0x01;
    internal const ushort JoystickUsage = 0x04;
    internal const ushort GamepadUsage = 0x05;
    internal const ushort HatSwitchUsage = 0x39;
    internal const ushort ButtonPage = 0x09;
    internal const uint InputCommand = 0x10000003;
    internal const uint PreparsedDataCommand = 0x20000005;
    internal const uint DeviceNameCommand = 0x20000007;
    internal const uint DeviceInfoCommand = 0x2000000B;
    internal const uint HidType = 2;
    internal const int HidpInputReport = 0;
    internal const int HidpStatusSuccess = 0x00110000;
    internal const uint Error = uint.MaxValue;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices, uint deviceCount, uint structureSize);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputData(
        IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetRawInputDeviceInfo(
        IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetRawInputDeviceInfo(
        IntPtr device, uint command, ref RawInputDeviceInfo data, ref uint size);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetUsagesEx(
        int reportType, ushort linkCollection,
        [Out] UsageAndPage[] buttonList, ref uint usageLength,
        IntPtr preparsedData, [In] byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetUsageValue(
        int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint usageValue, IntPtr preparsedData,
        [In] byte[] report, uint reportLength);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDevice
    {
        internal ushort UsagePage;
        internal ushort Usage;
        internal uint Flags;
        internal IntPtr TargetWindow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputHeader
    {
        internal uint Type;
        internal uint Size;
        internal IntPtr Device;
        internal IntPtr WordParameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawHid
    {
        internal uint SizeHid;
        internal uint Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDeviceInfo
    {
        internal uint Size;
        internal uint Type;
        internal RawInputDeviceInfoUnion Device;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct RawInputDeviceInfoUnion
    {
        [FieldOffset(0)]
        internal RawInputDeviceInfoHid Hid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDeviceInfoHid
    {
        internal uint VendorId;
        internal uint ProductId;
        internal uint VersionNumber;
        internal ushort UsagePage;
        internal ushort Usage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UsageAndPage
    {
        internal ushort Usage;
        internal ushort UsagePage;
    }
}
