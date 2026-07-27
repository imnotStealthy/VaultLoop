using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ReplayGlitchGTA;

internal static class GameProcessService
{
    private const string LegacyProcessName = "GTA5";
    private const string EnhancedProcessName = "GTA5_Enhanced";
    private const int MaximumExtendedPath = 32768;

    private static readonly string[] SupportedProcessNames =
        [EnhancedProcessName, LegacyProcessName];

    internal static bool TryGetVerifiedForegroundGame(out string executablePath)
    {
        return TryGetVerifiedForegroundGame(out executablePath, out _);
    }

    internal static bool TryGetVerifiedForegroundGame(
        out string executablePath, out IntPtr windowHandle)
    {
        executablePath = string.Empty;
        windowHandle = IntPtr.Zero;
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero ||
            NativeMethods.GetWindowThreadProcessId(foregroundWindow, out var processId) == 0 ||
            processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            if (!TryGetVerifiedProcessPath(process, out var candidatePath) ||
                process.HasExited ||
                NativeMethods.GetForegroundWindow() != foregroundWindow)
            {
                return false;
            }

            NativeMethods.GetWindowThreadProcessId(foregroundWindow, out var currentProcessId);
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

        var verifiedPath = string.Empty;
        var found = TryScanSupportedProcesses(process =>
        {
            if (!TryGetVerifiedProcessPath(process, out var candidatePath))
            {
                return false;
            }
            verifiedPath = candidatePath;
            return true;
        });

        executablePath = found ? verifiedPath : string.Empty;
        return found;
    }

    /// <summary>
    /// Walks the running processes carrying a supported game name and stops at the first one
    /// <paramref name="accept"/> takes. Every enumerated process is disposed, and a process
    /// list that cannot be read is skipped rather than aborting the scan.
    /// </summary>
    private static bool TryScanSupportedProcesses(Func<Process, bool> accept)
    {
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
                    if (accept(process))
                    {
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
        return false;
    }

    /// <summary>
    /// Finds a running, Authenticode-verified game process and reports its identifier along
    /// with its path. Used by the connection diagnostics, which need the process id to read
    /// the owning rows of the TCP table. Applies the same trust checks as every other entry
    /// point: an unverified process is never reported.
    /// </summary>
    internal static bool TryGetVerifiedGameProcess(out int processId, out string executablePath)
    {
        var verifiedId = 0;
        var verifiedPath = string.Empty;
        var found = TryScanSupportedProcesses(process =>
        {
            if (!TryGetVerifiedProcessPath(process, out var candidatePath))
            {
                return false;
            }
            try
            {
                verifiedId = process.Id;
            }
            catch
            {
                return false;
            }
            verifiedPath = candidatePath;
            return true;
        });

        processId = found ? verifiedId : 0;
        executablePath = found ? verifiedPath : string.Empty;
        return found;
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
            var hasFingerprint = TrustCache.TryGetFingerprint(
                file.SafeFileHandle, out var fingerprint);
            var now = Stopwatch.GetTimestamp();
            if (hasFingerprint &&
                TrustCache.TryGet(fullPath, fingerprint, now, out var cachedTrust))
            {
                return cachedTrust;
            }

            var trusted =
                AuthenticodeVerifier.IsSignatureValid(fullPath, file.SafeFileHandle) &&
                AuthenticodeVerifier.IsRockstarPublisher(fullPath);
            if (hasFingerprint)
            {
                TrustCache.Store(fullPath, fingerprint, trusted, now);
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
        expectedWindow != IntPtr.Zero && NativeMethods.GetForegroundWindow() == expectedWindow;

    private static bool TryGetVerifiedProcessPath(Process process, out string executablePath)
    {
        executablePath = string.Empty;
        try
        {
            if (process.HasExited || !IsSupportedProcessName(process.ProcessName))
            {
                return false;
            }

            var candidatePath = TryGetProcessImagePath(process);
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

    /// <summary>
    /// Resolves a running process's image path, preferring <see cref="Process.MainModule"/>
    /// and falling back to <c>QueryFullProcessImageName</c>.
    /// </summary>
    /// <remarks>
    /// Reading MainModule needs PROCESS_VM_READ, which anti-cheat protection on an online game
    /// process routinely denies even to an administrator — the game then looks absent to the
    /// detector. QueryFullProcessImageName needs only PROCESS_QUERY_LIMITED_INFORMATION, which
    /// survives that protection. This changes nothing about trust: whatever path comes back is
    /// still put through <see cref="IsTrustedGameExecutable"/>.
    /// </remarks>
    private static string? TryGetProcessImagePath(Process process)
    {
        try
        {
            var mainModulePath = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(mainModulePath))
            {
                return mainModulePath;
            }
        }
        catch
        {
            // Falls through to the limited-information query below.
        }

        int processId;
        try
        {
            processId = process.Id;
        }
        catch
        {
            return null;
        }

        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var buffer = new StringBuilder(MaximumExtendedPath);
            var size = buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? buffer.ToString()
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Explains, for every process whose name looks like GTA, why it is or is not accepted.
    /// Diagnostics only — it reports on the real checks and never relaxes them; the verdict
    /// line comes from <see cref="IsTrustedGameExecutable"/> itself.
    /// </summary>
    internal static IReadOnlyList<string> DescribeDetectionCandidates()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception exception)
        {
            return [$"process enumeration failed: {exception.Message}"];
        }

        var lines = new List<string>();
        try
        {
            foreach (var process in processes)
            {
                string processName;
                try
                {
                    processName = process.ProcessName;
                }
                catch
                {
                    continue;
                }
                if (processName.IndexOf("gta", StringComparison.OrdinalIgnoreCase) < 0 &&
                    !IsSupportedProcessName(processName))
                {
                    continue;
                }

                var processId = 0;
                try
                {
                    processId = process.Id;
                }
                catch
                {
                    // Reported as pid 0 below.
                }

                var path = TryGetProcessImagePath(process);
                if (string.IsNullOrWhiteSpace(path))
                {
                    lines.Add($"{processName} (pid {processId}): executable path unavailable");
                    continue;
                }
                lines.Add($"{processName} (pid {processId}): {path}");
                lines.Add($"    {DescribeTrust(path!)}");
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        if (lines.Count == 0)
        {
            lines.Add("no running process has a GTA-like name");
        }
        return lines;
    }

    private static string DescribeTrust(string executablePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(executablePath);
            var fileName = Path.GetFileNameWithoutExtension(fullPath);
            if (!Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return "rejected: not an .exe";
            }
            if (!IsSupportedProcessName(fileName))
            {
                return $"rejected: executable name '{fileName}' is not a supported game name " +
                       $"(expected {LegacyProcessName} or {EnhancedProcessName})";
            }

            using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.SequentialScan);
            if (!AuthenticodeVerifier.IsSignatureValid(fullPath, file.SafeFileHandle))
            {
                return "rejected: Authenticode signature did not verify";
            }

            string publisher;
            try
            {
                publisher = AuthenticodeVerifier.ReadPublisherName(fullPath);
            }
            catch (Exception exception)
            {
                return $"rejected: signer certificate unreadable ({exception.GetType().Name})";
            }

            return IsTrustedGameExecutable(fullPath)
                ? $"accepted: signed by '{publisher}'"
                : $"rejected: publisher is '{publisher}'";
        }
        catch (Exception exception)
        {
            return $"rejected: {exception.GetType().Name}: {exception.Message}";
        }
    }

}
