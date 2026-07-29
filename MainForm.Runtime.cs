using System;
using System.Collections.Generic;
using System.Threading;

namespace ReplayGlitchGTA;

internal sealed partial class MainForm
{
    private const int RefreshIntervalMilliseconds = 1200;

    /// <summary>Roughly six seconds of tolerance before auto-restoring on game loss.</summary>
    private const int MissingGameTicksBeforeRestore = 5;

    /// <summary>Two consecutive ticks, so a single scheduling hiccup cannot raise the alarm.</summary>
    private const int LeakingTicksBeforeWarning = 2;

    private int _runtimeRefreshInProgress;
    private int _runtimeRefreshVersion;
    private int _missingGameTicks;
    private int _leakingTicks;
    private bool _leakReported;
    private HashSet<int>? _blockedPortsAtActivation;

    private void QueueRuntimeRefresh()
    {
        if (_applying || _firewall is null ||
            Interlocked.Exchange(ref _runtimeRefreshInProgress, 1) != 0)
        {
            return;
        }

        var version = Interlocked.Increment(ref _runtimeRefreshVersion);
        _hotkeyHook.Disarm();
        _controllerShortcutService.Suspend();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var snapshot = ReadRuntimeSnapshot();
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(() => ApplyRuntimeSnapshot(snapshot, version)));
                    }
                }
                catch (InvalidOperationException)
                {
                    // The window closed between the handle check and BeginInvoke.
                }
            }
            finally
            {
                Interlocked.Exchange(ref _runtimeRefreshInProgress, 0);
            }
        });
    }

    private RuntimeSnapshot ReadRuntimeSnapshot()
    {
        var snapshot = new RuntimeSnapshot();
        if (GameProcessService.TryGetVerifiedForegroundGame(
                out var foregroundPath, out var foregroundWindow))
        {
            snapshot.ForegroundPath = foregroundPath;
            snapshot.ForegroundWindow = foregroundWindow;
        }
        else if (GameProcessService.TryFindVerifiedRunningGame(out var runningPath))
        {
            snapshot.RunningPath = runningPath;
        }

        try
        {
            snapshot.FirewallState = _firewall?.GetState();
        }
        catch (Exception exception)
        {
            snapshot.FirewallError = exception;
        }

        if (snapshot.FirewallState == FirewallRuleState.Active &&
            GameProcessService.TryGetVerifiedGameProcess(out var processId, out _))
        {
            snapshot.BlockedLocalPorts = ReadBlockedLocalPorts(processId);
        }
        return snapshot;
    }

    /// <summary>
    /// The local ports of the game's established connections to blocked addresses. Comparing
    /// this set across ticks tells an already-open flow — which a new block rule does not tear
    /// down — apart from a flow that completed its handshake through the active rule.
    /// </summary>
    private static HashSet<int> ReadBlockedLocalPorts(int processId)
    {
        var ports = new HashSet<int>();
        foreach (var connection in GameConnectionInspector.GetConnections(processId))
        {
            if (connection.State == TcpConnectionState.Established &&
                RockstarNetworks.IsBlocked(connection.RemoteAddress))
            {
                ports.Add(connection.LocalPort);
            }
        }
        return ports;
    }

    private void ApplyRuntimeSnapshot(RuntimeSnapshot snapshot, int version)
    {
        if (version != Volatile.Read(ref _runtimeRefreshVersion) || _applying)
        {
            return;
        }

        ApplyGameContext(
            snapshot.ForegroundPath, snapshot.ForegroundWindow, snapshot.RunningPath);

        if (snapshot.FirewallState.HasValue)
        {
            ApplyFirewallState(snapshot.FirewallState.Value);
        }
        else if (snapshot.FirewallError is not null)
        {
            SetUnknownState();
        }

        EvaluateBlockEffectiveness(snapshot);
        EvaluateGameLoss(snapshot.HasVerifiedGame);
    }

    /// <summary>
    /// Warns when the rule reports Active while the game keeps opening new connections to a
    /// blocked address. The first Active tick only records a baseline: connections that were
    /// already established when the rule went up survive it, and flagging those would cry wolf
    /// on every activation.
    /// </summary>
    private void EvaluateBlockEffectiveness(RuntimeSnapshot snapshot)
    {
        if (_firewallState != FirewallRuleState.Active || snapshot.BlockedLocalPorts is null)
        {
            _blockedPortsAtActivation = null;
            _leakingTicks = 0;
            _leakReported = false;
            return;
        }

        if (_blockedPortsAtActivation is null)
        {
            _blockedPortsAtActivation = snapshot.BlockedLocalPorts;
            return;
        }

        var hasNewConnection = false;
        foreach (var localPort in snapshot.BlockedLocalPorts)
        {
            if (!_blockedPortsAtActivation.Contains(localPort))
            {
                hasNewConnection = true;
                break;
            }
        }

        if (!hasNewConnection)
        {
            _leakingTicks = 0;
            return;
        }

        _leakingTicks++;
        if (_leakingTicks < LeakingTicksBeforeWarning || _leakReported)
        {
            return;
        }

        _leakReported = true;
        SetGameStatus("BLOCK NOT EFFECTIVE", Palette.HotPink);
        ShowStatusToast("BLOCK NOT EFFECTIVE", Palette.Yellow,
            "The rule is active but GTA opened a new connection to a blocked address. " +
            "Run --diagnose to see the endpoints in use.");
    }

    /// <summary>
    /// Restores the link when the verified game is gone while no-save is still active. The
    /// rule names the game executable by path, so leaving it in place would silently block a
    /// relaunched GTA — the exact failure this application exists to prevent, inverted.
    /// A few ticks of tolerance keep a brief detection gap from cutting no-save mid-activity.
    /// </summary>
    private void EvaluateGameLoss(bool hasVerifiedGame)
    {
        if (hasVerifiedGame || _firewallState != FirewallRuleState.Active)
        {
            _missingGameTicks = 0;
            return;
        }
        if (_applying || _firewall is null || !_stateKnown)
        {
            return;
        }

        _missingGameTicks++;
        if (_missingGameTicks < MissingGameTicksBeforeRestore)
        {
            return;
        }

        _missingGameTicks = 0;
        RestoreAfterGameLoss();
    }

    private void RestoreAfterGameLoss()
    {
        RunExclusive(
            () =>
            {
                _firewall!.SetNoSaveEnabled(false);
                SetDisplayedState(false);
                ShowStatusToast("NO-SAVE RESTORED", Palette.Acid,
                    "The verified GTA process is gone. No-save was disabled automatically.");
            },
            exception =>
                ShowStatusToast("AUTO-RESTORE FAILED", Palette.Yellow, exception.Message));
    }

    private void RefreshGameContext()
    {
        if (GameProcessService.TryGetVerifiedForegroundGame(
                out var foregroundPath, out var foregroundWindow))
        {
            ApplyGameContext(foregroundPath, foregroundWindow, runningPath: null);
            return;
        }

        ApplyGameContext(foregroundPath: null, IntPtr.Zero,
            GameProcessService.TryFindVerifiedRunningGame(out var runningPath)
                ? runningPath
                : null);
    }

    /// <summary>
    /// Publishes the detected game context: the shortcut is armed only for a verified game in
    /// the foreground, and the footer reports which of the three situations applies.
    /// </summary>
    private void ApplyGameContext(
        string? foregroundPath, IntPtr foregroundWindow, string? runningPath)
    {
        _hasVerifiedForegroundGame = foregroundPath is not null;
        UpdateHudVisibility();
        if (foregroundPath is not null)
        {
            _verifiedGamePath = foregroundPath;
            _hotkeyHook.Arm(foregroundWindow);
            _controllerShortcutService.Arm(foregroundWindow);
            SetGameStatus("GTA READY  //  SAFE RESTORE", Palette.Acid);
            return;
        }

        _hotkeyHook.Disarm();
        _controllerShortcutService.Disarm();
        _verifiedGamePath = runningPath;
        SetGameStatus(
            runningPath is null ? "WAITING FOR GTA" : "GTA IN BACKGROUND", Palette.Yellow);
    }

    private sealed class RuntimeSnapshot
    {
        internal string? ForegroundPath { get; set; }
        internal IntPtr ForegroundWindow { get; set; }
        internal string? RunningPath { get; set; }
        internal FirewallRuleState? FirewallState { get; set; }
        internal Exception? FirewallError { get; set; }
        internal HashSet<int>? BlockedLocalPorts { get; set; }

        internal bool HasVerifiedGame => ForegroundPath is not null || RunningPath is not null;
    }
}
