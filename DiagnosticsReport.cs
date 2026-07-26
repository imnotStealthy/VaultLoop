using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ReplayGlitchGTA;

/// <summary>
/// The <c>--diagnose</c> command. Reports what the managed rule blocks, what the firewall
/// currently holds, and which endpoints the verified game is actually talking to, so that an
/// <c>Active</c> state can be confirmed rather than trusted.
/// </summary>
/// <remarks>
/// Read-only. It reads firewall state and the TCP table; it never creates, modifies, or
/// removes a rule, and never touches a connection.
/// </remarks>
internal static class DiagnosticsReport
{
    private const int LeakingConnectionExitCode = 2;

    internal static int Run()
    {
        NativeMethods.AttachParentConsole();

        var report = new StringBuilder();
        report.AppendLine();
        report.AppendLine("VaultLoop diagnostics");
        report.AppendLine();

        report.AppendLine($"Blocked address set (from {RockstarNetworks.BlockedSource})");
        foreach (var prefix in RockstarNetworks.BlockedSet)
        {
            report.AppendLine($"  {prefix.Canonical}");
        }
        if (RockstarNetworks.BlockedConfigurationError is { } configurationError)
        {
            report.AppendLine($"  NOTE: {configurationError}");
        }
        report.AppendLine();

        var ruleState = ReadRuleState(report);
        var connections = ReadGameConnections(report);
        return Summarize(report, ruleState, connections);
    }

    private static FirewallRuleState? ReadRuleState(StringBuilder report)
    {
        report.AppendLine("Firewall");
        try
        {
            var state = new FirewallService().GetState();
            report.AppendLine($"  Managed rule state : {state}");
            report.AppendLine();
            return state;
        }
        catch (Exception exception)
        {
            report.AppendLine($"  Managed rule state : unavailable ({exception.Message})");
            report.AppendLine();
            return null;
        }
    }

    private static IReadOnlyList<GameConnection>? ReadGameConnections(StringBuilder report)
    {
        report.AppendLine("Game process");
        if (!GameProcessService.TryGetVerifiedGameProcess(out var processId, out var path))
        {
            report.AppendLine("  None. No verified Rockstar GTA V process is running.");
            report.AppendLine();
            report.AppendLine("Detection candidates");
            foreach (var line in GameProcessService.DescribeDetectionCandidates())
            {
                report.AppendLine($"  {line}");
            }
            report.AppendLine();
            return null;
        }

        report.AppendLine($"  {Path.GetFileName(path)} (pid {processId})");
        report.AppendLine($"  {path}");
        report.AppendLine();

        var connections = GameConnectionInspector.GetConnections(processId);
        report.AppendLine("Connections owned by the game");
        if (connections.Count == 0)
        {
            report.AppendLine("  None observed.");
            report.AppendLine();
            return connections;
        }

        report.AppendLine("  STATE         ENDPOINT                                    NETWORK        BLOCK SET");
        foreach (var connection in connections)
        {
            var network = RockstarNetworks.GetOnlineServiceName(connection.RemoteAddress)
                          ?? "-";
            var inBlockSet = RockstarNetworks.IsBlocked(connection.RemoteAddress)
                ? "in set"
                : "-";
            report.AppendLine(
                $"  {connection.State,-13} {connection.Endpoint,-43} {network,-14} {inBlockSet}");
        }
        report.AppendLine();
        return connections;
    }

    private static int Summarize(
        StringBuilder report, FirewallRuleState? ruleState,
        IReadOnlyList<GameConnection>? connections)
    {
        report.AppendLine("Verdict");
        var exitCode = 0;

        if (connections is null)
        {
            report.AppendLine(
                "  Start GTA V and rerun this command to observe its endpoints.");
        }
        else
        {
            var leaking = new List<GameConnection>();
            var uncovered = new List<GameConnection>();
            foreach (var connection in connections)
            {
                var isBlockedAddress = RockstarNetworks.IsBlocked(connection.RemoteAddress);
                if (isBlockedAddress && connection.State == TcpConnectionState.Established)
                {
                    leaking.Add(connection);
                }
                else if (!isBlockedAddress &&
                         RockstarNetworks.GetOnlineServiceName(connection.RemoteAddress) is not null)
                {
                    uncovered.Add(connection);
                }
            }

            if (ruleState == FirewallRuleState.Active && leaking.Count > 0)
            {
                report.AppendLine(
                    "  WARNING: the rule reports Active, but the game holds an established");
                report.AppendLine("  connection inside the blocked set:");
                foreach (var connection in leaking)
                {
                    report.AppendLine(
                        $"    {connection.Endpoint}  (local port {connection.LocalPort})");
                }
                report.AppendLine();
                report.AppendLine(
                    "  A block rule does not tear down a flow that was already open, so this is");
                report.AppendLine(
                    "  expected shortly after enabling no-save. It only proves the block is");
                report.AppendLine(
                    "  ineffective if the local port changes between two runs of this command,");
                report.AppendLine(
                    "  which means the game completed a new handshake through the active rule.");
                exitCode = LeakingConnectionExitCode;
            }
            else if (ruleState == FirewallRuleState.Active)
            {
                report.AppendLine(
                    "  The rule is Active and no established connection remains inside the");
                report.AppendLine("  blocked set.");
            }
            else
            {
                report.AppendLine(
                    $"  The managed rule is {(ruleState is null ? "unreadable" : ruleState.ToString())};" +
                    " the connections above are the unblocked baseline.");
            }

            if (uncovered.Count > 0)
            {
                report.AppendLine();
                report.AppendLine(
                    "  Rockstar endpoints outside the blocked set — candidates to widen the set,");
                report.AppendLine(
                    "  bearing in mind that blocking session traffic ends the online session:");
                foreach (var connection in uncovered)
                {
                    var network = RockstarNetworks.GetOnlineServiceName(connection.RemoteAddress);
                    report.AppendLine($"    {connection.Endpoint}  ({network})");
                }
            }
        }

        report.AppendLine();
        Console.WriteLine(report.ToString());
        return exitCode;
    }
}
