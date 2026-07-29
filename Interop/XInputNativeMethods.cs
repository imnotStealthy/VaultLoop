using System.Runtime.InteropServices;

namespace ReplayGlitchGTA;

internal static class XInputNativeMethods
{
    internal const uint Success = 0;
    internal const uint DeviceNotConnected = 1167;

    internal const ushort DPadUp = 0x0001;
    internal const ushort DPadDown = 0x0002;
    internal const ushort DPadLeft = 0x0004;
    internal const ushort DPadRight = 0x0008;
    internal const ushort Start = 0x0010;
    internal const ushort Back = 0x0020;
    internal const ushort LeftThumb = 0x0040;
    internal const ushort RightThumb = 0x0080;
    internal const ushort LeftShoulder = 0x0100;
    internal const ushort RightShoulder = 0x0200;
    internal const ushort A = 0x1000;
    internal const ushort B = 0x2000;
    internal const ushort X = 0x4000;
    internal const ushort Y = 0x8000;
    internal const byte TriggerThreshold = 30;

    [DllImport("xinput1_4.dll")]
    internal static extern uint XInputGetState(uint userIndex, out XInputState state);

    [StructLayout(LayoutKind.Sequential)]
    internal struct XInputState
    {
        internal uint PacketNumber;
        internal XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XInputGamepad
    {
        internal ushort Buttons;
        internal byte LeftTrigger;
        internal byte RightTrigger;
        internal short ThumbLeftX;
        internal short ThumbLeftY;
        internal short ThumbRightX;
        internal short ThumbRightY;
    }
}
