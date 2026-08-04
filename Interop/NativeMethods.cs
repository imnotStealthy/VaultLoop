using System;
using System.Runtime.InteropServices;
using System.Text;

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

    /// <summary>
    /// Enough access to read a process image path, and no more. Anti-cheat protection on the
    /// running game denies the broader rights that <c>Process.MainModule</c> needs.
    /// </summary>
    internal const int ProcessQueryLimitedInformation = 0x1000;

    private const int ParentProcessConsole = -1;
    private const int LoadLibrarySearchSystem32 = 0x00000800;

    /// <summary>
    /// Removes the executable's own directory from the DLL search path, so imported modules
    /// can only be resolved from System32.
    /// </summary>
    /// <remarks>
    /// VaultLoop runs elevated, and <c>wintrust.dll</c> and <c>iphlpapi.dll</c> are not
    /// KnownDLLs — without this, the directory holding VaultLoop.exe is searched before
    /// System32. Anyone able to write next to the executable could therefore have their own
    /// DLL loaded into an administrator process the next time the user accepts the elevation
    /// prompt. The release build is a single self-contained executable with no application
    /// DLLs of its own, so restricting the search path costs nothing.
    /// Must run before any other P/Invoke. Failure is not actionable and is ignored: on a
    /// system old enough to lack the API there is nothing better to fall back to.
    /// </remarks>
    internal static void RestrictDllSearchPathToSystem32()
    {
        try
        {
            SetDefaultDllDirectories(LoadLibrarySearchSystem32);
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }
    }

    /// <summary>
    /// Attaches this WinExe process to the console that launched it, so a command-line
    /// diagnostic can write where the caller can read it. Failure is expected and harmless
    /// when the process was started without a console.
    /// </summary>
    internal static void AttachParentConsole() => AttachConsole(ParentProcessConsole);

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

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(int directoryFlags);

    /// <summary>
    /// The live state of one virtual key. The low-level hook only knows the transitions it was
    /// given; this reports what the keyboard looks like now, including the transitions it
    /// missed.
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(
        IntPtr windowHandle, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(
        int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool QueryFullProcessImageName(
        IntPtr process, int flags, StringBuilder executableName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

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
