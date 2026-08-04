using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

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
    private const int XInputSlots = 4;
    private const int SampleMilliseconds = 4000;
    private const int SampleIntervalMilliseconds = 30;

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
        ReadControllerState(report);
        var connections = ReadGameConnections(report);
        return Summarize(report, ruleState, connections);
    }

    /// <summary>
    /// Reports what the application actually reads from an Xbox controller, and whether it
    /// matches the configured combination. A shortcut that does nothing gives the user no way
    /// to tell an unread controller from a combination that is read but does not match — a
    /// trigger resting above its threshold, or a button pressed alongside it, is enough,
    /// because the match is exact by design.
    /// </summary>
    /// <remarks>
    /// XInput only. DualShock and DualSense controllers are fed by raw input messages, which
    /// require the running window and its message loop, so this command cannot sample them.
    /// </remarks>
    private static void ReadControllerState(StringBuilder report)
    {
        report.AppendLine("Controller");
        var shortcut = ControllerShortcutSettings.Load();
        report.AppendLine(shortcut is null
            ? "  Configured shortcut : none"
            : $"  Configured shortcut : {shortcut.Format()}");

        var connectedSlots = new List<int>();
        for (var slot = 0; slot < XInputSlots; slot++)
        {
            if (!TryReadXInput(slot, out _, out var available))
            {
                if (!available)
                {
                    report.AppendLine("  XInput              : unavailable on this system");
                    report.AppendLine();
                    return;
                }
                continue;
            }
            connectedSlots.Add(slot);
        }

        if (connectedSlots.Count == 0)
        {
            report.AppendLine("  Connected XInput    : none");
            report.AppendLine(shortcut?.DeviceKind == ControllerDeviceKind.XInput
                ? "  The configured Xbox controller is not connected, so the shortcut cannot fire."
                : "  Connect an Xbox controller, or use the window to configure one.");
            report.AppendLine();
            return;
        }

        foreach (var slot in connectedSlots)
        {
            report.AppendLine(
                $"  Connected XInput    : {ControllerShortcut.FormatDeviceName(ControllerDeviceKind.XInput, $"xinput:{slot}")}");
            // A trigger counts as a pressed button above its threshold. Printing the analog
            // value tells a trigger the user is pulling from one that rests high on its own,
            // which is otherwise invisible and defeats the exact match on every poll.
            if (TryReadXInputTriggers(slot, out var leftTrigger, out var rightTrigger))
            {
                report.AppendLine(
                    $"  Trigger values      : left {leftTrigger}, right {rightTrigger} " +
                    $"(a trigger counts as pressed at {XInputNativeMethods.TriggerThreshold} of 255)");
            }
        }
        if (shortcut?.DeviceKind != ControllerDeviceKind.XInput)
        {
            report.AppendLine();
            return;
        }

        SampleConfiguredShortcut(report, shortcut);
        report.AppendLine();
    }

    /// <summary>
    /// Samples the configured slot for a few seconds and reports every distinct combination
    /// the user managed to press, so an unexpected extra button is visible instead of guessed.
    /// </summary>
    private static void SampleConfiguredShortcut(
        StringBuilder report, ControllerShortcut shortcut)
    {
        var slot = ControllerShortcut.ParseXInputSlot(shortcut.DeviceId);
        if (!TryReadXInput(slot, out _, out _))
        {
            report.AppendLine(
                $"  {shortcut.DisplayName} is not connected, so the shortcut cannot fire.");
            return;
        }

        Console.WriteLine(
            $"Hold {ControllerShortcut.FormatButtons(shortcut.DeviceKind, shortcut.Buttons)} " +
            $"on {shortcut.DisplayName} now ({SampleMilliseconds / 1000} s sample)...");

        // The application drops the analog inputs a device rests on, so the sample applies the
        // same rule: a report that ignored it would contradict what the shortcut then does.
        var observed = new List<ControllerButtons>();
        var matched = false;
        ControllerButtons? stuckAnalog = null;
        var resting = ControllerButtons.None;
        for (var elapsed = 0; elapsed < SampleMilliseconds; elapsed += SampleIntervalMilliseconds)
        {
            if (TryReadXInput(slot, out var rawButtons, out _))
            {
                var buttons = ControllerShortcutService.TrackStuckAnalogInputs(
                    rawButtons, stuckAnalog, out resting);
                stuckAnalog = resting;
                if (buttons != ControllerButtons.None && !observed.Contains(buttons))
                {
                    observed.Add(buttons);
                    matched |= ControllerShortcut.IsExactCombination(buttons, shortcut.Buttons);
                }
            }
            Thread.Sleep(SampleIntervalMilliseconds);
        }

        if (resting != ControllerButtons.None)
        {
            report.AppendLine(
                $"  Resting analog      : {ControllerShortcut.FormatButtons(shortcut.DeviceKind, resting)} " +
                "never went below its threshold and is ignored as a pressed button");
        }
        if (observed.Count == 0)
        {
            report.AppendLine("  Sample              : no button was read during the sample");
            return;
        }

        foreach (var buttons in observed)
        {
            var verdict = ControllerShortcut.IsExactCombination(buttons, shortcut.Buttons)
                ? "matches the configured shortcut"
                : "does not match";
            report.AppendLine(
                $"  Sample              : {ControllerShortcut.FormatButtons(shortcut.DeviceKind, buttons)} — {verdict}");
        }
        report.AppendLine(matched
            ? "  The combination is read exactly. In game, hold it for 500 ms with VaultLoop " +
              "elevated and GTA V in the foreground."
            : "  The exact combination was never read. The match is exact: any extra button, " +
              "or a trigger resting above its threshold, prevents it.");
    }

    private static bool TryReadXInputTriggers(
        int slot, out byte leftTrigger, out byte rightTrigger)
    {
        leftTrigger = 0;
        rightTrigger = 0;
        try
        {
            if (XInputNativeMethods.XInputGetState((uint)slot, out var state) !=
                XInputNativeMethods.Success)
            {
                return false;
            }
            leftTrigger = state.Gamepad.LeftTrigger;
            rightTrigger = state.Gamepad.RightTrigger;
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads one XInput slot. <paramref name="available"/> reports whether XInput itself can be
    /// called at all, which is a different failure from a slot with nothing plugged into it.
    /// </summary>
    private static bool TryReadXInput(
        int slot, out ControllerButtons buttons, out bool available)
    {
        buttons = ControllerButtons.None;
        available = true;
        if (slot is < 0 or >= XInputSlots)
        {
            return false;
        }

        uint result;
        XInputNativeMethods.XInputState state;
        try
        {
            result = XInputNativeMethods.XInputGetState((uint)slot, out state);
        }
        catch (DllNotFoundException)
        {
            available = false;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            available = false;
            return false;
        }

        if (result != XInputNativeMethods.Success)
        {
            return false;
        }
        buttons = ControllerShortcutService.MapXInputButtons(state.Gamepad);
        return true;
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
