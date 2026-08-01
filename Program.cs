using System;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

#if NETFRAMEWORK
[assembly: AssemblyTitle("VaultLoop")]
[assembly: AssemblyDescription("GTA V no-save firewall link controller")]
[assembly: AssemblyProduct("VaultLoop")]
[assembly: AssemblyVersion("1.2.5.0")]
[assembly: AssemblyFileVersion("1.2.5.0")]
#endif

namespace ReplayGlitchGTA;

internal static class Program
{
    [STAThread]
    private static void Main(string[] arguments)
    {
        // First statement in the process: every later P/Invoke must resolve from System32.
        NativeMethods.RestrictDllSearchPathToSystem32();

        var startupLaunch = IsStartupLaunch(arguments);
        var elevatedRequest = TryParseElevatedRequest(arguments, out var parentProcessId,
            out var requestedGamePath, out var requestedForegroundWindow);
        if (elevatedRequest)
        {
            WaitForParentExit(parentProcessId);
        }

        if (IsCommand(arguments, "--watchdog") && arguments.Length == 2)
        {
            RunWatchdog(arguments[1]);
            return;
        }

        InitializeApplication();
        if (IsCommand(arguments, "--restore") && arguments.Length == 1)
        {
            if (IsRunningAsAdministrator())
            {
                RunEmergencyRestore();
            }
            else
            {
                try
                {
                    StartElevated("--restore");
                }
                catch (Exception exception)
                {
                    MessageBox.Show($"The firewall rule could not be restored:\n{exception.Message}",
                        "VaultLoop restore failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            return;
        }

        if (IsCommand(arguments, "--diagnose") && arguments.Length == 1)
        {
            Environment.ExitCode = DiagnosticsReport.Run();
            return;
        }

        if (!HasSupportedRuntime())
        {
            MessageBox.Show(
                ".NET Framework 4.8 or later is required to run VaultLoop.",
                "VaultLoop runtime required", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

#if DEBUG
        if (IsCommand(arguments, "--selftest"))
        {
            Environment.ExitCode = SelfTest.Run();
            return;
        }

        if (IsCommand(arguments, "--render-preview") && arguments.Length >= 2)
        {
            var enabled = arguments.Length >= 3 &&
                          arguments[2].Equals("on", StringComparison.OrdinalIgnoreCase);
            var unknown = arguments.Length >= 3 &&
                          arguments[2].Equals("unknown", StringComparison.OrdinalIgnoreCase);
            using var preview = new MainForm(null, previewMode: true,
                previewState: enabled, previewUnknown: unknown);
            preview.Show();
            Application.DoEvents();
            preview.SavePreview(arguments[1]);
            return;
        }
#endif

        using var singleInstance = new Mutex(true, @"Global\ReplayGlitchGTA.NoSave", out var ownsMutex);
        if (!ownsMutex)
        {
            if (!startupLaunch)
            {
                MessageBox.Show("The application is already running.", "VaultLoop",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        var firewall = new FirewallService();
        var startupOutcome = PrepareFirewall(
            firewall, requestedGamePath, requestedForegroundWindow, startupLaunch);
        if (startupOutcome == StartupOutcome.HandedOverToElevatedProcess)
        {
            singleInstance.ReleaseMutex();
            return;
        }

        if (IsRunningAsAdministrator())
        {
            StartWatchdog();
        }
        try
        {
            if (startupOutcome == StartupOutcome.RecoveredStaleRule)
            {
                MessageBox.Show(
                    "VaultLoop restored a firewall rule left by a previous interrupted session.",
                    "Previous session recovered", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            using var mainForm = new MainForm(firewall);
            using var applicationContext = new ApplicationContext();
            mainForm.FormClosed += (_, _) => applicationContext.ExitThread();
            if (startupLaunch)
            {
                mainForm.StartInTray();
            }
            else
            {
                mainForm.Show();
            }
            Application.Run(applicationContext);
        }
        finally
        {
            if (IsRunningAsAdministrator())
            {
                try
                {
                    firewall.SetNoSaveEnabled(false);
                }
                catch (Exception exception)
                {
                    // The form already reports cleanup failures; this is a final best-effort
                    // retry, and the only trace it can leave is the activity log.
                    ActivityLog.Write("final restore on exit failed", exception);
                }
            }
            singleInstance.ReleaseMutex();
        }
    }

    private static bool IsCommand(string[] arguments, string name) =>
        arguments.Length >= 1 &&
        arguments[0].Equals(name, StringComparison.OrdinalIgnoreCase);

    internal static bool IsStartupLaunch(string[] arguments) =>
        arguments.Length == 1 && IsCommand(arguments, "--startup") ||
        arguments.Length >= 3 &&
        arguments[arguments.Length - 1].Equals(
            "--startup", StringComparison.OrdinalIgnoreCase) &&
        TryParseElevatedRequest(arguments, out _, out _, out _);

    /// <summary>
    /// Brings the firewall to a known state before the window opens: a rule surviving a
    /// previous session is removed, and an activation requested by an elevation relaunch is
    /// applied. A rule left behind while the process is unelevated can only be dealt with by
    /// the elevated instance, which this one then hands over to.
    /// </summary>
    private static StartupOutcome PrepareFirewall(
        FirewallService firewall, string? requestedGamePath, IntPtr requestedForegroundWindow,
        bool startupLaunch)
    {
        var outcome = StartupOutcome.Ready;
        try
        {
            if (firewall.GetState() != FirewallRuleState.Inactive)
            {
                if (!IsRunningAsAdministrator())
                {
                    try
                    {
                        RelaunchElevated(null, IntPtr.Zero, startupLaunch);
                    }
                    catch (Exception exception)
                    {
                        ReportStartupFailure(exception);
                    }
                    return StartupOutcome.HandedOverToElevatedProcess;
                }
                firewall.SetNoSaveEnabled(false);
                ActivityLog.Write(
                    "startup recovery: removed a rule left by a previous session");
                outcome = StartupOutcome.RecoveredStaleRule;
            }

            if (requestedGamePath is not null)
            {
                if (requestedForegroundWindow != IntPtr.Zero &&
                    !GameProcessService.IsCurrentForegroundWindow(requestedForegroundWindow))
                {
                    throw new InvalidOperationException(
                        "GTA V must remain in the foreground to use the shortcut.");
                }
                firewall.SetNoSaveEnabled(true, requestedGamePath);
            }
        }
        catch (Exception exception)
        {
            ReportStartupFailure(exception);
        }
        return outcome;
    }

    private static void ReportStartupFailure(Exception exception)
    {
        ActivityLog.Write("startup recovery failed", exception);
        MessageBox.Show(
            $"VaultLoop could not validate or restore its firewall rule:\n{exception.Message}",
            "Startup recovery failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private enum StartupOutcome
    {
        Ready,
        RecoveredStaleRule,
        HandedOverToElevatedProcess
    }

    internal static void RelaunchElevated(
        string? gamePath, IntPtr foregroundWindow, bool startupLaunch = false)
    {
        ActivityLog.Write(gamePath is null
            ? "requesting administrator rights"
            : "requesting administrator rights to enable no-save");
        StartElevated(BuildElevatedArguments(
            Process.GetCurrentProcess().Id, gamePath, foregroundWindow, startupLaunch));
    }

    internal static string BuildElevatedArguments(
        int parentProcessId, string? gamePath, IntPtr foregroundWindow,
        bool startupLaunch = false)
    {
        var arguments = $"--elevated {parentProcessId}";
        if (gamePath is not null)
        {
            arguments += $" --enable \"{gamePath}\"";
            if (foregroundWindow != IntPtr.Zero)
            {
                arguments += $" --foreground-window {foregroundWindow.ToInt64()}";
            }
        }
        return startupLaunch ? $"{arguments} --startup" : arguments;
    }

    internal static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void StartElevated(string arguments)
    {
        using var elevatedProcess = Process.Start(new ProcessStartInfo
        {
            FileName = Application.ExecutablePath,
            Arguments = arguments,
            Verb = "runas",
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("The elevated VaultLoop process could not start.");
    }

    internal static bool TryParseElevatedRequest(
        string[] arguments, out int parentProcessId, out string? gamePath,
        out IntPtr foregroundWindow)
    {
        parentProcessId = 0;
        gamePath = null;
        foregroundWindow = IntPtr.Zero;
        var argumentCount = arguments.Length;
        if (argumentCount >= 3 &&
            arguments[argumentCount - 1].Equals(
                "--startup", StringComparison.OrdinalIgnoreCase))
        {
            argumentCount--;
        }
        if (argumentCount is not (2 or 4 or 6) ||
            !arguments[0].Equals("--elevated", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(arguments[1], out parentProcessId))
        {
            return false;
        }

        if (argumentCount >= 4)
        {
            if (!arguments[2].Equals("--enable", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(arguments[3]))
            {
                return false;
            }
            gamePath = arguments[3];
        }
        if (argumentCount >= 6)
        {
            if (!arguments[4].Equals(
                    "--foreground-window", StringComparison.OrdinalIgnoreCase) ||
                !long.TryParse(arguments[5], out var windowHandle))
            {
                return false;
            }
            foregroundWindow = new IntPtr(windowHandle);
        }
        return true;
    }

    private static void WaitForParentExit(int parentProcessId)
    {
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            parent.WaitForExit();
        }
        catch
        {
            // The parent may already be gone; the elevated process can continue.
        }
    }

    private static void StartWatchdog()
    {
        try
        {
            using var watchdog = Process.Start(new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                Arguments = $"--watchdog {Process.GetCurrentProcess().Id}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Crash recovery could not be started:\n{exception.Message}\n\n" +
                "Normal-exit restoration remains active.",
                "Watchdog unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void RunWatchdog(string processIdText)
    {
        try
        {
            if (int.TryParse(processIdText, out var processId))
            {
                using var parent = Process.GetProcessById(processId);
                parent.WaitForExit();
            }
        }
        catch
        {
            // Cleanup must run even if the process handle cannot be opened or waited.
        }
        finally
        {
            RestoreFirewallWithRetries();
        }
    }

    private static void RestoreFirewallWithRetries()
    {
        var firewall = new FirewallService();
        Exception? lastFailure = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                firewall.SetNoSaveEnabled(false);
                ActivityLog.Write("watchdog restored the Rockstar link after the window exited");
                return;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
                Thread.Sleep(250);
            }
        }
        if (lastFailure is not null)
        {
            ActivityLog.Write("watchdog could not restore the Rockstar link", lastFailure);
        }
    }

    private static void RunEmergencyRestore()
    {
        try
        {
            var firewall = new FirewallService();
            firewall.SetNoSaveEnabled(false);
            ActivityLog.Write("--restore removed the managed rule");
            MessageBox.Show("The VaultLoop firewall rule is inactive.", "VaultLoop restore",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            ActivityLog.Write("--restore failed", exception);
            MessageBox.Show($"The firewall rule could not be restored:\n{exception.Message}",
                "VaultLoop restore failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void InitializeApplication()
    {
#if NETFRAMEWORK
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
#else
        ApplicationConfiguration.Initialize();
#endif
    }

    internal static bool HasSupportedRuntime()
    {
        const int NetFramework48Release = 528040;
        try
        {
            var release = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full",
                "Release", null);
            return release is not null && Convert.ToInt32(release) >= NetFramework48Release;
        }
        catch
        {
            return false;
        }
    }
}
