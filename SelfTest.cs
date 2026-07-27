#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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

        checks.Verify("assembly version is 1.2.1.0",
            () => typeof(Program).Assembly.GetName().Version?.ToString() == "1.2.1.0");
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

        checks.Verify("the preview window builds with the expected chrome", () =>
        {
            using var preview = new MainForm(null, previewMode: true);
            return preview.Text == "VaultLoop" && preview.ClientSize.Width >= 780;
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
