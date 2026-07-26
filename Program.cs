using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

#if NETFRAMEWORK
[assembly: AssemblyTitle("VaultLoop")]
[assembly: AssemblyDescription("GTA V no-save firewall link controller")]
[assembly: AssemblyProduct("VaultLoop")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]
#endif

namespace ReplayGlitchGTA;

internal static class Program
{
    [STAThread]
    private static void Main(string[] arguments)
    {
        // First statement in the process: every later P/Invoke must resolve from System32.
        NativeMethods.RestrictDllSearchPathToSystem32();

        if (arguments.Length == 2 &&
            arguments[0].Equals("--watchdog", StringComparison.OrdinalIgnoreCase))
        {
            RunWatchdog(arguments[1]);
            return;
        }

        InitializeApplication();
        if (arguments.Length == 1 &&
            arguments[0].Equals("--restore", StringComparison.OrdinalIgnoreCase))
        {
            RunEmergencyRestore();
            return;
        }

        if (arguments.Length == 1 &&
            arguments[0].Equals("--diagnose", StringComparison.OrdinalIgnoreCase))
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
        if (arguments.Length >= 1 &&
            arguments[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            Environment.ExitCode = SelfTest.Run();
            return;
        }

        if (arguments.Length >= 2 &&
            arguments[0].Equals("--render-preview", StringComparison.OrdinalIgnoreCase))
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
            MessageBox.Show("The application is already running.", "VaultLoop",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var firewall = new FirewallService();
        var recoveredStaleRule = false;
        try
        {
            var startupState = firewall.GetState();
            if (startupState != FirewallRuleState.Inactive)
            {
                firewall.SetNoSaveEnabled(false);
                recoveredStaleRule = true;
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"VaultLoop could not validate or restore its firewall rule:\n{exception.Message}",
                "Startup recovery failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        StartWatchdog();
        try
        {
            if (recoveredStaleRule)
            {
                MessageBox.Show(
                    "VaultLoop restored a firewall rule left by a previous interrupted session.",
                    "Previous session recovered", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            Application.Run(new MainForm(firewall));
        }
        finally
        {
            try
            {
                firewall.SetNoSaveEnabled(false);
            }
            catch
            {
                // The form already reports cleanup failures; this is a final best-effort retry.
            }
            singleInstance.ReleaseMutex();
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
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                firewall.SetNoSaveEnabled(false);
                return;
            }
            catch
            {
                Thread.Sleep(250);
            }
        }
    }

    private static void RunEmergencyRestore()
    {
        try
        {
            var firewall = new FirewallService();
            firewall.SetNoSaveEnabled(false);
            MessageBox.Show("The VaultLoop firewall rule is inactive.", "VaultLoop restore",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
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
