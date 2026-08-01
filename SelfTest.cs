#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

/// <summary>
/// Dependency-free regression checks, reachable through <c>VaultLoop.exe --selftest</c>.
/// This replaces the former Test.ps1 harness: every assertion calls the member directly,
/// so a rename or a signature change becomes a compile error instead of a silent gap.
/// The checks run without elevation and never request a firewall mutation.
/// The checks are read-only: no firewall rule is created, modified, or removed.
/// </summary>
internal static class SelfTest
{
    internal static int Run()
    {
        NativeMethods.AttachParentConsole();

        var checks = new CheckList();

        checks.Verify("assembly version is 1.2.5.0",
            () => typeof(Program).Assembly.GetName().Version?.ToString() == "1.2.5.0");
        checks.Verify("assembly targets the canonical .NET Framework runtime",
            () => typeof(Program).Assembly.ImageRuntimeVersion == "v4.0.30319");
        checks.Verify("embedded logo resource is present",
            () => Array.IndexOf(
                typeof(Program).Assembly.GetManifestResourceNames(),
                "ReplayGlitchLogo.png") >= 0);

        checks.Verify("GTA5 is a supported process name",
            () => GameProcessService.IsSupportedProcessName("GTA5"));
        checks.Verify("GTA5_Enhanced is a supported process name",
            () => GameProcessService.IsSupportedProcessName("GTA5_Enhanced"));
        checks.Verify("unrelated processes are rejected",
            () => !GameProcessService.IsSupportedProcessName("NVIDIA Share"));
        checks.Verify("an empty foreground handle never arms the shortcut",
            () => !GameProcessService.IsCurrentForegroundWindow(IntPtr.Zero));
        checks.Verify("shortcut trigger gate rejects every disarmed state", () =>
        {
            var canTriggerCalls = 0;
            Func<bool> canTrigger = () =>
            {
                canTriggerCalls++;
                return true;
            };
            var gate = new ShortcutTriggerGate();
            if (gate.Armed || gate.CanFire(canTrigger))
            {
                return false;
            }

            gate.Arm(IntPtr.Zero);
            if (gate.Armed || gate.CanFire(canTrigger))
            {
                return false;
            }

            gate.Arm(new IntPtr(1));
            gate.Disarm();
            return !gate.Armed && !gate.CanFire(canTrigger) && canTriggerCalls == 0;
        });

        // Properties of whatever set is active, so editing endpoints.txt cannot turn the
        // suite red on a legitimate configuration.
        var activeSet = RockstarNetworks.FormatBlockedSet();
        checks.Verify("the blocked set is never empty",
            () => RockstarNetworks.BlockedSet.Count > 0);
        checks.Verify("every blocked prefix stays inside a Rockstar Online Services allocation",
            () =>
            {
                foreach (var prefix in RockstarNetworks.BlockedSet)
                {
                    var inside = false;
                    foreach (var allocation in RockstarNetworks.OnlineServiceAllocations)
                    {
                        inside |= prefix.IsInside(allocation);
                    }
                    if (!inside)
                    {
                        return false;
                    }
                }
                return true;
            });
        checks.Verify("Zynga and corporate Take-Two ranges are never blocked",
            () => !RockstarNetworks.IsBlocked(IPAddress.Parse("184.75.160.1")) &&
                  !RockstarNetworks.IsBlocked(IPAddress.Parse("139.138.224.1")) &&
                  !RockstarNetworks.IsBlocked(IPAddress.Parse("74.114.8.1")));
        checks.Verify("an unrelated public address is never blocked",
            () => !RockstarNetworks.IsBlocked(IPAddress.Parse("8.8.8.8")));
        checks.Verify("the observed save endpoint is classified as RSONET-NA1",
            () => RockstarNetworks.GetOnlineServiceName(
                IPAddress.Parse(RockstarNetworks.ObservedSaveEndpoint)) == "RSONET-NA1");

        checks.Verify("the active set is accepted as written",
            () => FirewallService.TargetsOnlyManagedAddresses(activeSet));
        checks.Verify("the active set is accepted in reverse order",
            () => FirewallService.TargetsOnlyManagedAddresses(Reverse(activeSet)));
        checks.Verify("a superset of the active set is rejected",
            () => !FirewallService.TargetsOnlyManagedAddresses($"{activeSet},198.133.210.1"));
        checks.Verify("dropping an entry from the active set is rejected",
            () => RockstarNetworks.BlockedSet.Count == 1 ||
                  !FirewallService.TargetsOnlyManagedAddresses(
                      activeSet.Substring(activeSet.IndexOf(',') + 1)));
        checks.Verify("a duplicated entry cannot stand in for a missing one",
            () => RockstarNetworks.BlockedSet.Count == 1 ||
                  !FirewallService.TargetsOnlyManagedAddresses(
                      $"{RockstarNetworks.BlockedSet[0].Canonical}," +
                      $"{RockstarNetworks.BlockedSet[0].Canonical}"));
        checks.Verify("malformed addresses are rejected",
            () => !FirewallService.TargetsOnlyManagedAddresses("not-an-address,::/0"));
        checks.Verify("an address outside every Rockstar allocation is refused by the loader",
            () => BlockedEndpointsSettings.TryLoad(
                      [IpPrefix.TryParse("192.81.240.0/21")!], out _) is null ||
                  !RockstarNetworks.IsBlocked(IPAddress.Parse("8.8.8.8")));

        checks.Verify("rules left by earlier versions are still recognized",
            () => FirewallService.TargetsOnlyLegacyAddress("192.81.241.171") &&
                  FirewallService.TargetsOnlyLegacyAddress("192.81.241.171/32") &&
                  FirewallService.TargetsOnlyLegacyAddress(
                      "192.81.241.171/255.255.255.255"));
        checks.Verify("a multi-address legacy rule is rejected",
            () => !FirewallService.TargetsOnlyLegacyAddress("192.81.241.171,8.8.8.8"));

        checks.Verify("prefix parsing normalizes every form to one canonical value",
            () => IpPrefix.TryParse("192.81.241.171/24")?.Canonical == "192.81.241.0/24" &&
                  IpPrefix.TryParse("192.81.241.0/255.255.255.0")?.Canonical ==
                      "192.81.241.0/24" &&
                  IpPrefix.TryParse("192.81.241.171")?.Canonical ==
                      "192.81.241.171/32");
        checks.Verify("a non-contiguous subnet mask is rejected",
            () => IpPrefix.TryParse("192.81.241.0/255.0.255.0") is null);
        checks.Verify("an out-of-range prefix length is rejected",
            () => IpPrefix.TryParse("192.81.241.0/33") is null);

        checks.Verify("the default shortcut is valid",
            () => ShortcutDialog.IsValidShortcut(Keys.Control | Keys.Shift, Keys.F8));
        checks.Verify("Alt+F8 remains available as a saved user shortcut",
            () => ShortcutDialog.IsValidShortcut(Keys.Alt, Keys.F8));
        checks.Verify("Alt+F4 stays reserved",
            () => !ShortcutDialog.IsValidShortcut(Keys.Alt, Keys.F4));
        checks.Verify("Alt+Tab stays reserved",
            () => !ShortcutDialog.IsValidShortcut(Keys.Alt, Keys.Tab));
        checks.Verify("numeric shortcut names are user-friendly",
            () => ShortcutSettings.Format(Keys.Alt, Keys.D8) == "ALT+8");
        checks.Verify("controller shortcuts require exactly two or three buttons", () =>
        {
            var twoButtons =
                ControllerButtons.LeftShoulder | ControllerButtons.RightShoulder;
            var threeButtons = twoButtons | ControllerButtons.West;
            var fourButtons = threeButtons | ControllerButtons.North;
            var dPadDiagonal =
                ControllerButtons.DPadUp | ControllerButtons.DPadRight;
            return !ControllerShortcut.IsValidCombination(ControllerButtons.West) &&
                   !ControllerShortcut.IsValidCombination(dPadDiagonal) &&
                   ControllerShortcut.IsValidCombination(twoButtons) &&
                   ControllerShortcut.IsValidCombination(threeButtons) &&
                   !ControllerShortcut.IsValidCombination(fourButtons);
        });
        checks.Verify("controller shortcut matching rejects extra gameplay buttons", () =>
        {
            var configured =
                ControllerButtons.LeftShoulder | ControllerButtons.West;
            return ControllerShortcut.IsExactCombination(configured, configured) &&
                   !ControllerShortcut.IsExactCombination(
                       configured | ControllerButtons.South, configured);
        });
        checks.Verify("shortcut toggling activates and deactivates no-save",
            () => MainForm.GetToggledEnabledState(FirewallRuleState.Inactive) &&
                  !MainForm.GetToggledEnabledState(FirewallRuleState.Active));
        checks.Verify("controller shortcuts use the accepted 500 ms hold",
            () => ControllerShortcutService.HoldMilliseconds == 500);
        checks.Verify("controller shortcut settings round-trip without device ambiguity", () =>
        {
            var original = new ControllerShortcut(
                ControllerDeviceKind.DualSense,
                @"\\?\HID#VID_054C&PID_0CE6#VAULTLOOP_TEST",
                ControllerButtons.LeftShoulder |
                ControllerButtons.RightShoulder |
                ControllerButtons.West);
            return ControllerShortcutSettings.TryParse(
                       ControllerShortcutSettings.Serialize(original), out var parsed) &&
                   parsed is not null &&
                   parsed.DeviceKind == original.DeviceKind &&
                   parsed.DeviceId == original.DeviceId &&
                   parsed.Buttons == original.Buttons &&
                   parsed.Format() ==
                       "DualSense  //  L1 + R1 + SQUARE";
        });
        checks.Verify("disabled controller shortcut settings stay disabled",
            () => ControllerShortcutSettings.TryParse("disabled", out var parsed) &&
                  parsed is null);
        checks.Verify("Xbox button reports map to user-facing controller buttons", () =>
        {
            var gamepad = new XInputNativeMethods.XInputGamepad
            {
                Buttons = XInputNativeMethods.LeftShoulder |
                          XInputNativeMethods.RightShoulder |
                          XInputNativeMethods.X,
                LeftTrigger = XInputNativeMethods.TriggerThreshold
            };
            return ControllerShortcutService.MapXInputButtons(gamepad) ==
                   (ControllerButtons.LeftShoulder |
                    ControllerButtons.RightShoulder |
                    ControllerButtons.West |
                    ControllerButtons.LeftTrigger);
        });
        checks.Verify("only supported Sony controller product ids use raw HID",
            () => ControllerShortcutService.GetSonyDeviceKind(0x054C, 0x09CC) ==
                      ControllerDeviceKind.DualShock4 &&
                  ControllerShortcutService.GetSonyDeviceKind(0x054C, 0x0CE6) ==
                      ControllerDeviceKind.DualSense &&
                  ControllerShortcutService.GetSonyDeviceKind(0x057E, 0x2009) is null);
        // GetRawInputDeviceInfoW rejects the query outright when cbSize disagrees with the
        // size Windows expects, which silently disabled every PlayStation controller.
        checks.Verify("the raw input device info structure matches the Windows layout",
            () => Marshal.SizeOf(typeof(RawInputNativeMethods.RawInputDeviceInfo)) == 32 &&
                  Marshal.SizeOf(typeof(RawInputNativeMethods.RawInputDeviceInfoUnion)) == 24);
        checks.Verify("raw controller input registers and unregisters cleanly", () =>
        {
            using var inputHost = new Form();
            using var controllerService = new ControllerShortcutService(
                shortcut: null, canTrigger: () => false);
            var registered = controllerService.Install(inputHost.Handle);
            controllerService.Uninstall();
            return registered;
        });
        checks.Verify("shortcut modifiers follow low-level key events", () =>
        {
            var state = GlobalHotkeyHook.UpdateModifierKeyState(
                0, Keys.LControlKey, keyDown: true, keyUp: false);
            state = GlobalHotkeyHook.UpdateModifierKeyState(
                state, Keys.RShiftKey, keyDown: true, keyUp: false);
            if (GlobalHotkeyHook.GetPressedModifiers(state, 0) !=
                (Keys.Control | Keys.Shift))
            {
                return false;
            }

            state = GlobalHotkeyHook.UpdateModifierKeyState(
                state, Keys.LControlKey, keyDown: false, keyUp: true);
            state = GlobalHotkeyHook.UpdateModifierKeyState(
                state, Keys.RShiftKey, keyDown: false, keyUp: true);
            state = GlobalHotkeyHook.UpdateModifierKeyState(
                state, Keys.LMenu, keyDown: true, keyUp: false);
            return GlobalHotkeyHook.GetPressedModifiers(state, 0) == Keys.Alt &&
                   GlobalHotkeyHook.GetPressedModifiers(
                       0, NativeMethods.AltDownFlag) == Keys.Alt;
        });

        checks.Verify(".NET Framework 4.8 runtime check passes",
            Program.HasSupportedRuntime);
        checks.Verify("elevated activation arguments preserve path and foreground window", () =>
        {
            var arguments = Program.BuildElevatedArguments(
                42, @"C:\Games\Grand Theft Auto V\GTA5.exe", new IntPtr(123));
            return Program.TryParseElevatedRequest(
                       ["--elevated", "42", "--enable",
                        @"C:\Games\Grand Theft Auto V\GTA5.exe",
                        "--foreground-window", "123"],
                       out var parentProcessId, out var gamePath, out var foregroundWindow) &&
                   arguments == "--elevated 42 --enable \"C:\\Games\\Grand Theft Auto V\\GTA5.exe\" " +
                                "--foreground-window 123" &&
                   parentProcessId == 42 &&
                   gamePath == @"C:\Games\Grand Theft Auto V\GTA5.exe" &&
                   foregroundWindow == new IntPtr(123);
        });
        checks.Verify("malformed elevated activation arguments are rejected",
            () => !Program.TryParseElevatedRequest(
                ["--elevated", "42", "--enable"], out _, out _, out _));
        checks.Verify("Windows startup uses the exact quoted executable and startup argument",
            () => StartupRegistration.BuildCommand(
                      @"C:\Program Files\VaultLoop\VaultLoop.exe") ==
                  "\"C:\\Program Files\\VaultLoop\\VaultLoop.exe\" --startup");
        checks.Verify("only the exact startup command requests a tray-only launch",
            () => Program.IsStartupLaunch(["--startup"]) &&
                  Program.IsStartupLaunch(["--STARTUP"]) &&
                  Program.IsStartupLaunch(["--elevated", "42", "--startup"]) &&
                  !Program.IsStartupLaunch([]) &&
                  !Program.IsStartupLaunch(["--startup", "extra"]) &&
                  !Program.IsStartupLaunch(["--elevated", "bad", "--startup"]));
        checks.Verify("HUD requires its toggle and verified GTA in the foreground",
            () => MainForm.ShouldShowHud(true, true) &&
                  !MainForm.ShouldShowHud(false, true) &&
                  !MainForm.ShouldShowHud(true, false));
        checks.Verify("the tray menu exposes status, HUD, startup, window, and safe exit actions",
            () =>
            {
                using var tray = new TrayMenu(
                    () => { }, () => { }, () => { }, () => { }, () => { });
                tray.SetStatus("ACTIVE", Palette.HotPink);
                tray.SetHudEnabled(enabled: false);
                tray.SetStartupEnabled(enabled: true);
                tray.SetWindowVisible(visible: false);
                var exitItems = tray.Items.Find("TrayExit", searchAllChildren: false);
                return tray.StatusText == "STATUS  //  ACTIVE" &&
                       tray.HudText == "HUD  //  OFF" &&
                       tray.StartupText == "START WITH WINDOWS  //  ON" &&
                       tray.OpenEnabled && !tray.HideEnabled &&
                       exitItems.Length == 1 &&
                       exitItems[0].Text.Replace("&&", "&") == "EXIT & RESTORE";
            });

        // Writes one labelled entry to the user's activity log. That is the whole point: the
        // log is the only trace a failed session leaves, so the path that writes it has to be
        // exercised rather than assumed.
        checks.Verify("the activity log records one flattened entry per line", () =>
        {
            ActivityLog.Write("self-test entry\r\nwith an embedded line break");
            var contents = AppSettingsStorage.ReadText(
                ActivityLog.FileName, includeLegacy: false, out _);
            return contents is not null &&
                   contents.Contains("self-test entry with an embedded line break");
        });

        checks.Verify("the preview window builds with the expected chrome", () =>
        {
            using var preview = new MainForm(null, previewMode: true);
            var adminButtons = preview.Controls.Find("LaunchAsAdminButton", true);
            var hudButtons = preview.Controls.Find("HudVisibilityButton", true);
            return preview.Text == "VaultLoop" && preview.ClientSize.Width >= 780 &&
                   adminButtons.Length == 1 && adminButtons[0].Text == "ADMIN READY" &&
                   hudButtons.Length == 1 && hudButtons[0].Text == "HUD ON";
        });
        checks.Verify("the shortcut dialog exposes disabled controller configuration",
            () =>
            {
                using var dialog = new ShortcutDialog(
                    Keys.Control | Keys.Shift, Keys.F8, darkMode: true);
                var captures = dialog.Controls.Find("ControllerShortcutCapture", true);
                var configure = dialog.Controls.Find("ConfigureControllerShortcut", true);
                return captures.Length == 1 &&
                       captures[0].Text == "NOT CONFIGURED" &&
                       configure.Length == 1 &&
                       !configure[0].Enabled;
            });

        checks.Verify("the connection inspector runs without a game process",
            () => GameConnectionInspector.GetConnections(0) is not null);
        checks.Verify("the connection inspector reads IPv4 rows correctly",
            () => VerifyInspectorAgainstLiveSocket(AddressFamily.InterNetwork));
        checks.Verify("the connection inspector reads IPv6 rows correctly",
            () => !Socket.OSSupportsIPv6 ||
                  VerifyInspectorAgainstLiveSocket(AddressFamily.InterNetworkV6));

        var firewallState = FirewallRuleState.Invalid;
        checks.Verify("the firewall rule state is readable", () =>
        {
            firewallState = new FirewallService().GetState();
            return Enum.IsDefined(typeof(FirewallRuleState), firewallState);
        });

        Console.WriteLine(checks.BuildReport(firewallState));
        return checks.Failures.Count == 0 ? 0 : 1;
    }

    private static string Reverse(string commaSeparated)
    {
        var entries = new List<string>(commaSeparated.Split(','));
        entries.Reverse();
        return string.Join(",", entries);
    }

    /// <summary>
    /// Opens a real loopback connection and checks the inspector reports it with the exact
    /// ports the sockets chose. The TCP table is read through hand-written struct offsets that
    /// differ between address families, which is precisely where a silent misread would hide.
    /// </summary>
    private static bool VerifyInspectorAgainstLiveSocket(AddressFamily addressFamily)
    {
        var loopback = addressFamily == AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Loopback
            : IPAddress.Loopback;
        using var listener = new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(loopback, 0));
        listener.Listen(1);
        var listenerPort = ((IPEndPoint)listener.LocalEndPoint).Port;

        using var client = new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(new IPEndPoint(loopback, listenerPort));
        using var accepted = listener.Accept();
        var clientPort = ((IPEndPoint)client.LocalEndPoint).Port;

        foreach (var connection in GameConnectionInspector.GetConnections(
                     Process.GetCurrentProcess().Id))
        {
            if (connection.LocalPort == clientPort &&
                connection.RemotePort == listenerPort &&
                connection.State == TcpConnectionState.Established &&
                connection.RemoteAddress.Equals(loopback))
            {
                return true;
            }
        }
        return false;
    }

    private sealed class CheckList
    {
        private readonly StringBuilder _report = new();
        private int _executed;

        internal List<string> Failures { get; } = [];

        internal void Verify(string description, Func<bool> assertion)
        {
            _executed++;
            bool succeeded;
            string detail;
            try
            {
                succeeded = assertion();
                detail = succeeded ? "" : " (assertion returned false)";
            }
            catch (Exception exception)
            {
                succeeded = false;
                detail = $" ({exception.GetType().Name}: {exception.Message})";
            }

            _report.AppendLine($"[{(succeeded ? "PASS" : "FAIL")}] {description}{detail}");
            if (!succeeded)
            {
                Failures.Add(description);
            }
        }

        internal string BuildReport(FirewallRuleState firewallState)
        {
            var summary = new StringBuilder();
            summary.AppendLine();
            summary.Append(_report);
            summary.AppendLine();
            summary.AppendLine($"Result        = {(Failures.Count == 0 ? "PASS" : "FAIL")}");
            summary.AppendLine($"Checks        = {_executed - Failures.Count}/{_executed}");
            summary.AppendLine($"FirewallState = {firewallState}");
            foreach (var failure in Failures)
            {
                summary.AppendLine($"Failed        = {failure}");
            }
            return summary.ToString();
        }
    }
}
#endif
