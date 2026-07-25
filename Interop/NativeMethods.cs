using System;
using System.Runtime.InteropServices;

namespace ReplayGlitchGTA;

internal static class NativeMethods
{
    internal const int LowLevelKeyboardHook = 13;
    internal const int KeyDownMessage = 0x0100;
    internal const int KeyUpMessage = 0x0101;
    internal const int SystemKeyDownMessage = 0x0104;
    internal const int SystemKeyUpMessage = 0x0105;
    internal const uint LowerIntegrityInjectedFlag = 0x02;
    internal const uint InjectedFlag = 0x10;
    internal const uint AltDownFlag = 0x20;
    internal const int NonClientLeftButtonDown = 0x00A1;
    internal const int HitCaption = 0x0002;

    internal delegate IntPtr LowLevelKeyboardProcedure(
        int code, IntPtr wordParameter, IntPtr longParameter);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(
        int hookId, LowLevelKeyboardProcedure procedure, IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(
        IntPtr hookHandle, int code, IntPtr wordParameter, IntPtr longParameter);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(
        IntPtr windowHandle, int message, int wordParameter, int longParameter);

    [StructLayout(LayoutKind.Sequential)]
    internal struct LowLevelKeyboardData
    {
        internal uint VirtualKeyCode;
        internal uint ScanCode;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }
}
