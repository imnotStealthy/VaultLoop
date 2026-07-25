using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace ReplayGlitchGTA;

internal static class GameProcessService
{
    private const string LegacyProcessName = "GTA5";
    private const string EnhancedProcessName = "GTA5_Enhanced";
    private const int MaximumCacheEntries = 16;
    private const int TrustedCacheLifetimeSeconds = 30;
    private const int RejectedCacheLifetimeSeconds = 15;
    private const uint WinTrustUiNone = 2;
    private const uint WinTrustRevokeWholeChain = 1;
    private const uint WinTrustChoiceFile = 1;
    private const uint WinTrustStateActionIgnore = 0;
    private const uint WinTrustRevocationCheckChainExcludeRoot = 0x80;
    private const uint WinTrustDisableMd2Md4 = 0x2000;
    private const int FileBasicInfoClass = 0;

    private static readonly string[] SupportedProcessNames =
        [EnhancedProcessName, LegacyProcessName];
    private static readonly Guid GenericVerifyV2Action =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, TrustCacheEntry> TrustCache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static bool TryGetVerifiedForegroundGame(out string executablePath)
    {
        return TryGetVerifiedForegroundGame(out executablePath, out _);
    }

    internal static bool TryGetVerifiedForegroundGame(
        out string executablePath, out IntPtr windowHandle)
    {
        executablePath = string.Empty;
        windowHandle = IntPtr.Zero;
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero ||
            GetWindowThreadProcessId(foregroundWindow, out var processId) == 0 ||
            processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            if (!TryGetVerifiedProcessPath(process, out var candidatePath) ||
                process.HasExited ||
                GetForegroundWindow() != foregroundWindow)
            {
                return false;
            }

            GetWindowThreadProcessId(foregroundWindow, out var currentProcessId);
            if (currentProcessId != processId)
            {
                return false;
            }

            executablePath = candidatePath;
            windowHandle = foregroundWindow;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryFindVerifiedRunningGame(out string executablePath)
    {
        if (TryGetVerifiedForegroundGame(out executablePath))
        {
            return true;
        }

        foreach (var processName in SupportedProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            try
            {
                foreach (var process in processes)
                {
                    if (TryGetVerifiedProcessPath(process, out var candidatePath))
                    {
                        executablePath = candidatePath;
                        return true;
                    }
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        executablePath = string.Empty;
        return false;
    }

    internal static bool IsTrustedGameExecutable(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(executablePath);
            if (!Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                !IsSupportedProcessName(Path.GetFileNameWithoutExtension(fullPath)))
            {
                return false;
            }

            using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.SequentialScan);
            var hasFingerprint = TryGetFingerprint(file.SafeFileHandle, out var fingerprint);
            var now = Stopwatch.GetTimestamp();
            if (hasFingerprint &&
                TryGetCachedTrust(fullPath, fingerprint, now, out var cachedTrust))
            {
                return cachedTrust;
            }

            var trusted = VerifyAuthenticode(fullPath, file.SafeFileHandle) &&
                          IsRockstarPublisher(fullPath);
            if (hasFingerprint)
            {
                CacheTrust(fullPath, fingerprint, trusted, now);
            }
            return trusted;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsSupportedProcessName(string processName) =>
        string.Equals(processName, LegacyProcessName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(processName, EnhancedProcessName, StringComparison.OrdinalIgnoreCase);

    internal static bool IsCurrentForegroundWindow(IntPtr expectedWindow) =>
        expectedWindow != IntPtr.Zero && GetForegroundWindow() == expectedWindow;

    private static bool TryGetVerifiedProcessPath(Process process, out string executablePath)
    {
        executablePath = string.Empty;
        try
        {
            if (process.HasExited || !IsSupportedProcessName(process.ProcessName))
            {
                return false;
            }

            var candidatePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(candidatePath))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(candidatePath);
            if (!IsTrustedGameExecutable(fullPath) || process.HasExited)
            {
                return false;
            }

            executablePath = fullPath;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool VerifyAuthenticode(string executablePath, SafeFileHandle fileHandle)
    {
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)),
            FilePath = executablePath,
            FileHandle = fileHandle.DangerousGetHandle(),
            KnownSubject = IntPtr.Zero
        };
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                UiChoice = WinTrustUiNone,
                RevocationChecks = WinTrustRevokeWholeChain,
                UnionChoice = WinTrustChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = WinTrustStateActionIgnore,
                ProviderFlags = WinTrustRevocationCheckChainExcludeRoot |
                                WinTrustDisableMd2Md4
            };
            var action = GenericVerifyV2Action;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref trustData) == 0;
        }
        finally
        {
            Marshal.DestroyStructure(fileInfoPointer, typeof(WinTrustFileInfo));
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    private static bool IsRockstarPublisher(string executablePath)
    {
        using var signer = X509Certificate.CreateFromSignedFile(executablePath);
        using var certificate = new X509Certificate2(signer);
        var publisher = certificate.GetNameInfo(X509NameType.SimpleName, false).Trim();
        return publisher.Equals("Rockstar Games, Inc.", StringComparison.OrdinalIgnoreCase) ||
               publisher.Equals("Rockstar Games, Inc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetFingerprint(
        SafeFileHandle fileHandle, out FileFingerprint fingerprint)
    {
        fingerprint = default;
        if (!GetFileInformationByHandle(fileHandle, out var standardInfo) ||
            !GetFileInformationByHandleEx(fileHandle, FileBasicInfoClass,
                out var basicInfo, (uint)Marshal.SizeOf(typeof(FileBasicInfo))))
        {
            return false;
        }

        fingerprint = new FileFingerprint(
            standardInfo.VolumeSerialNumber,
            ((ulong)standardInfo.FileIndexHigh << 32) | standardInfo.FileIndexLow,
            ((ulong)standardInfo.FileSizeHigh << 32) | standardInfo.FileSizeLow,
            basicInfo.LastWriteTime,
            basicInfo.ChangeTime);
        return true;
    }

    private static bool TryGetCachedTrust(
        string path, FileFingerprint fingerprint, long now, out bool trusted)
    {
        lock (CacheLock)
        {
            if (TrustCache.TryGetValue(path, out var entry) &&
                entry.Fingerprint.Equals(fingerprint) &&
                now < entry.ExpiresAt)
            {
                trusted = entry.Trusted;
                return true;
            }
        }

        trusted = false;
        return false;
    }

    private static void CacheTrust(
        string path, FileFingerprint fingerprint, bool trusted, long now)
    {
        var lifetime = trusted
            ? TrustedCacheLifetimeSeconds
            : RejectedCacheLifetimeSeconds;
        var expiresAt = now + Stopwatch.Frequency * lifetime;
        lock (CacheLock)
        {
            if (TrustCache.Count >= MaximumCacheEntries && !TrustCache.ContainsKey(path))
            {
                TrustCache.Clear();
            }
            TrustCache[path] = new TrustCacheEntry(fingerprint, trusted, expiresAt);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr windowHandle, out uint processId);

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle, ref Guid actionId, ref WinTrustData trustData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle, out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle, int fileInformationClass,
        out FileBasicInfo fileInformation, uint bufferSize);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        internal uint StructSize;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string FilePath;

        internal IntPtr FileHandle;
        internal IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        internal uint StructSize;
        internal IntPtr PolicyCallbackData;
        internal IntPtr SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal IntPtr FileInfo;
        internal uint StateAction;
        internal IntPtr StateData;
        internal IntPtr UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
        internal IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal NativeFileTime CreationTime;
        internal NativeFileTime LastAccessTime;
        internal NativeFileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal long ChangeTime;
        internal uint FileAttributes;
    }

    private readonly struct FileFingerprint : IEquatable<FileFingerprint>
    {
        private readonly uint _volumeSerialNumber;
        private readonly ulong _fileIndex;
        private readonly ulong _fileSize;
        private readonly long _lastWriteTime;
        private readonly long _changeTime;

        internal FileFingerprint(
            uint volumeSerialNumber, ulong fileIndex, ulong fileSize,
            long lastWriteTime, long changeTime)
        {
            _volumeSerialNumber = volumeSerialNumber;
            _fileIndex = fileIndex;
            _fileSize = fileSize;
            _lastWriteTime = lastWriteTime;
            _changeTime = changeTime;
        }

        public bool Equals(FileFingerprint other) =>
            _volumeSerialNumber == other._volumeSerialNumber &&
            _fileIndex == other._fileIndex &&
            _fileSize == other._fileSize &&
            _lastWriteTime == other._lastWriteTime &&
            _changeTime == other._changeTime;
    }

    private sealed class TrustCacheEntry
    {
        internal FileFingerprint Fingerprint { get; }
        internal bool Trusted { get; }
        internal long ExpiresAt { get; }

        internal TrustCacheEntry(
            FileFingerprint fingerprint, bool trusted, long expiresAt)
        {
            Fingerprint = fingerprint;
            Trusted = trusted;
            ExpiresAt = expiresAt;
        }
    }
}
