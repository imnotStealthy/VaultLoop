using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

internal sealed partial class MainForm : Form
{
    private const int RefreshIntervalMilliseconds = 1200;

    /// <summary>Roughly six seconds of tolerance before auto-restoring on game loss.</summary>
    private const int MissingGameTicksBeforeRestore = 5;

    /// <summary>Two consecutive ticks, so a single scheduling hiccup cannot raise the alarm.</summary>
    private const int LeakingTicksBeforeWarning = 2;

    private readonly FirewallService? _firewall;
    private readonly BooleanToggle _toggle;
    private readonly Label _stateKicker;
    private readonly Label _stateTitle;
    private readonly Label _stateDetail;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly bool _previewMode;
    private readonly bool _isAdministrator;
    private readonly Image _logoImage;
    private readonly GlobalHotkeyHook _hotkeyHook;
    private readonly ControllerShortcutService _controllerShortcutService;
    private readonly MiniHudForm? _miniHud;
    private NotifyIcon? _trayIcon;
    private TrayMenu? _trayMenu;
    private readonly Button _shortcutBadge;
    private readonly Button _shortcutFooter;
    private readonly Button _themeButton;
    private readonly Button _hudVisibilityButton;
    private readonly Label _gameStatusLabel;
    private readonly ThemeController _themeController;
    private Color _stateColor = Palette.Acid;

    // Read from the keyboard hook thread through the _canTrigger delegate, written on the UI
    // thread. Without volatile the hook can observe a stale value, swallow the keystroke, and
    // post a toggle that then no-ops on the UI thread — the key press disappears silently.
    private volatile bool _applying;
    private volatile bool _stateKnown = true;

    private bool _darkMode;
    private bool _hudEnabled = true;
    private bool _hasVerifiedForegroundGame;
    private bool _hotkeyRegistered;
    private bool _controllerRawInputRegistered;
    private bool _trayHintShown;
    private int _runtimeRefreshInProgress;
    private int _runtimeRefreshVersion;
    private FirewallRuleState _firewallState = FirewallRuleState.Inactive;
    private string? _verifiedGamePath;
    private int _missingGameTicks;
    private int _leakingTicks;
    private bool _leakReported;
    private HashSet<int>? _blockedPortsAtActivation;

    internal MainForm(FirewallService? firewall, bool previewMode = false,
        bool previewState = false, bool previewUnknown = false)
    {
        _firewall = firewall;
        _previewMode = previewMode;
        _isAdministrator = _previewMode || Program.IsRunningAsAdministrator();
        _logoImage = LoadLogo();
        var shortcut = ShortcutSettings.Load();
        _hotkeyHook = new GlobalHotkeyHook(
            shortcut.Modifiers, shortcut.Key,
            () => _isAdministrator && !_applying && _stateKnown);
        _hotkeyHook.Pressed += HandleHotkeyPressed;
        _controllerShortcutService = new ControllerShortcutService(
            ControllerShortcutSettings.Load(),
            () => _isAdministrator && !_applying && _stateKnown);
        _controllerShortcutService.Pressed += HandleHotkeyPressed;
        _darkMode = ThemeSettings.Load();

        var chrome = BuildLayout();
        _shortcutBadge = chrome.ShortcutBadge;
        _themeButton = chrome.ThemeButton;
        _toggle = chrome.Toggle;
        _stateKicker = chrome.StateKicker;
        _stateTitle = chrome.StateTitle;
        _stateDetail = chrome.StateDetail;
        _shortcutFooter = chrome.ShortcutFooter;
        _hudVisibilityButton = chrome.HudVisibilityButton;
        _gameStatusLabel = chrome.GameStatusLabel;
        _miniHud = _previewMode ? null : new MiniHudForm();
        if (!_previewMode)
        {
            InitializeTray();
        }

        _refreshTimer = new System.Windows.Forms.Timer
        {
            Interval = RefreshIntervalMilliseconds
        };
        _refreshTimer.Tick += (_, _) => QueueRuntimeRefresh();
        FormClosing += HandleClosing;
        Shown += HandleShown;
        Resize += (_, _) =>
        {
            if (!_previewMode && WindowState == FormWindowState.Minimized)
            {
                HideToTray();
            }
        };
        _themeController = new ThemeController(
            this, _themeButton, [_stateKicker, _stateTitle, _stateDetail, _toggle]);
        _themeController.CaptureThemeColors();
        _themeController.ApplyTheme(_darkMode);

        if (_previewMode)
        {
            if (previewUnknown)
            {
                SetUnknownState();
            }
            else
            {
                SetDisplayedState(previewState);
            }
        }
        else
        {
            RefreshRuntimeState(showErrors: true);
            _refreshTimer.Start();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!_previewMode)
        {
            _hotkeyRegistered = _hotkeyHook.Install();
            _controllerRawInputRegistered =
                _controllerShortcutService.Install(Handle);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (!_previewMode)
        {
            _controllerShortcutService.Uninstall();
            _hotkeyHook.Uninstall();
            _hotkeyRegistered = false;
            _controllerRawInputRegistered = false;
        }
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
            _miniHud?.Dispose();
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            _trayMenu?.Dispose();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            _controllerShortcutService.Dispose();
            _logoImage.Dispose();
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (!_previewMode)
        {
            if (message.Msg == RawInputNativeMethods.InputMessage)
            {
                _controllerShortcutService.ProcessRawInput(message.LParam);
            }
            else if (message.Msg == RawInputNativeMethods.InputDeviceChangeMessage)
            {
                _controllerShortcutService.ProcessRawInputDeviceChange(
                    message.LParam, message.WParam.ToInt32());
            }
        }
        base.WndProc(ref message);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.None;
        DrawCard(e.Graphics, HeaderCard, Palette.Yellow, Palette.Ink);
        DrawCard(e.Graphics, BodyCard,
            _darkMode ? Palette.DarkSurface : Palette.Paper, Palette.Blue);
        DrawCard(e.Graphics, StatusCard, _stateColor, Palette.Ink);
        DrawCard(e.Graphics, FooterCard, Palette.Ink, Palette.Blue);
        using var borderPen = new Pen(Palette.Ink, 3F);
        e.Graphics.DrawRectangle(borderPen, 1, 1, ClientSize.Width - 3, ClientSize.Height - 3);
    }

    internal void SavePreview(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, ClientSize));
        bitmap.Save(fullPath, ImageFormat.Png);
    }

    private void HandleShown(object? sender, EventArgs eventArgs)
    {
        _trayMenu?.SetWindowVisible(visible: true);
        UpdateHudVisibility();
        if (!_previewMode && !_hotkeyRegistered)
        {
            MessageBox.Show(this,
                $"The {ShortcutText} keyboard hook could not be installed.\n" +
                "The on-screen toggle remains available.",
                "Shortcut unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        if (!_previewMode && !_controllerRawInputRegistered &&
            _controllerShortcutService.Shortcut?.DeviceKind is
                ControllerDeviceKind.DualShock4 or ControllerDeviceKind.DualSense)
        {
            MessageBox.Show(this,
                "PlayStation controller input could not be registered.\n" +
                "The keyboard and Xbox shortcuts remain available.",
                "Controller shortcut unavailable",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void HandleHotkeyPressed(object? sender, EventArgs eventArgs)
    {
        if (!IsDisposed && IsHandleCreated)
        {
            BeginInvoke(new Action(() => ToggleState(fromHotkey: true)));
        }
    }

    private void RefreshRuntimeState(bool showErrors = false)
    {
        RefreshGameContext();
        RefreshState(showErrors);
    }

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

    private void SetGameStatus(string text, Color background)
    {
        _gameStatusLabel.Text = text;
        _gameStatusLabel.BackColor = background;
    }

    private string ShortcutText
    {
        get
        {
            var shortcut = _hotkeyHook.Shortcut;
            return ShortcutSettings.Format(shortcut.Modifiers, shortcut.Key);
        }
    }

    private void ConfigureShortcut()
    {
        _hotkeyHook.CapturingShortcut = true;
        try
        {
            var shortcut = _hotkeyHook.Shortcut;
            using var dialog = new ShortcutDialog(
                shortcut.Modifiers, shortcut.Key, _darkMode,
                _controllerShortcutService, _controllerShortcutService.Shortcut);
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var newModifiers = dialog.ShortcutModifiers;
            var newKey = dialog.ShortcutKey;
            ShortcutSettings.Save(newModifiers, newKey);
            ControllerShortcutSettings.Save(dialog.ControllerShortcut);
            _hotkeyHook.Shortcut = (newModifiers, newKey);
            _controllerShortcutService.Shortcut = dialog.ControllerShortcut;
            _shortcutBadge.Text = ShortcutText;
            _shortcutFooter.Text = $"{ShortcutText}  //  GTA ONLY";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"The shortcut could not be saved:\n{exception.Message}",
                "Shortcut error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _controllerShortcutService.CancelCapture();
            _hotkeyHook.CapturingShortcut = false;
        }
    }

    private void LaunchAsAdministrator()
    {
        if (_isAdministrator)
        {
            return;
        }

        try
        {
            Program.RelaunchElevated(null, IntPtr.Zero);
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this,
                $"VaultLoop could not be launched as administrator:\n{exception.Message}",
                "Administrator launch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ToggleHudVisibility()
    {
        _hudEnabled = !_hudEnabled;
        _hudVisibilityButton.Text = _hudEnabled ? "HUD ON" : "HUD OFF";
        _hudVisibilityButton.AccessibleName =
            _hudEnabled ? "Hide the no-save HUD" : "Show the no-save HUD";
        _trayMenu?.SetHudEnabled(_hudEnabled);
        UpdateHudVisibility();
    }

    private void UpdateHudVisibility()
    {
        if (_miniHud is null)
        {
            return;
        }

        if (ShouldShowHud(_hudEnabled, _hasVerifiedForegroundGame))
        {
            if (!_miniHud.Visible)
            {
                _miniHud.ShowOnActiveScreen();
            }
        }
        else if (_miniHud.Visible)
        {
            _miniHud.Hide();
        }
    }

    internal static bool ShouldShowHud(
        bool hudEnabled, bool hasVerifiedForegroundGame) =>
        hudEnabled && hasVerifiedForegroundGame;

    internal void StartInTray()
    {
        if (_previewMode)
        {
            return;
        }

        ShowInTaskbar = false;
        _ = Handle;
        _trayMenu?.SetWindowVisible(visible: false);
        UpdateHudVisibility();
    }

    private void InitializeTray()
    {
        _trayMenu = new TrayMenu(
            ShowFromTray, HideToTray, ToggleHudVisibility,
            ToggleStartWithWindows, ExitFromTray);
        _trayMenu.SetHudEnabled(_hudEnabled);
        _trayMenu.SetStartupEnabled(
            StartupRegistration.IsEnabled(Application.ExecutablePath));
        _trayMenu.SetWindowVisible(visible: false);

        _trayIcon = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Text = "VaultLoop - No-save starting",
            ContextMenuStrip = _trayMenu,
            Visible = true,
            BalloonTipTitle = "VaultLoop",
            BalloonTipText = "VaultLoop is still running in the system tray.",
            BalloonTipIcon = ToolTipIcon.Info
        };
        _trayIcon.MouseDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                ShowFromTray();
            }
        };
    }

    private void ToggleStartWithWindows()
    {
        try
        {
            var enable = !StartupRegistration.IsEnabled(Application.ExecutablePath);
            StartupRegistration.SetEnabled(Application.ExecutablePath, enable);
            _trayMenu?.SetStartupEnabled(enable);
        }
        catch (Exception exception)
        {
            ShowFromTray();
            MessageBox.Show(this,
                $"Start with Windows could not be updated:\n{exception.Message}",
                "Startup setting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void MinimizeToTray()
    {
        if (_previewMode)
        {
            WindowState = FormWindowState.Minimized;
            return;
        }
        HideToTray();
    }

    private void HideToTray()
    {
        if (!Visible)
        {
            return;
        }

        ShowInTaskbar = false;
        Hide();
        _trayMenu?.SetWindowVisible(visible: false);
        UpdateHudVisibility();
        if (!_trayHintShown && _trayIcon is not null)
        {
            _trayHintShown = true;
            _trayIcon.ShowBalloonTip(1600);
        }
    }

    private void ShowFromTray()
    {
        if (IsDisposed)
        {
            return;
        }

        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Show();
        Activate();
        BringToFront();
        _trayMenu?.SetWindowVisible(visible: true);
        UpdateHudVisibility();
    }

    private void ExitFromTray()
    {
        ShowFromTray();
        Close();
    }

    private void ToggleTheme()
    {
        _darkMode = !_darkMode;
        _themeController.ApplyTheme(_darkMode);
        try
        {
            ThemeSettings.Save(_darkMode);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this,
                $"The theme changed for this session but could not be saved:\n{exception.Message}",
                "Theme preference", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ToggleState(bool fromHotkey = false)
    {
        if (!_stateKnown)
        {
            return;
        }
        ApplyState(GetToggledEnabledState(_firewallState), fromHotkey);
    }

    internal static bool GetToggledEnabledState(FirewallRuleState currentState) =>
        currentState == FirewallRuleState.Inactive;

    private void ApplyState(bool enabled, bool fromHotkey = false)
    {
        if (!_stateKnown)
        {
            return;
        }

        RunExclusive(() => MutateState(enabled, fromHotkey), exception =>
        {
            if (fromHotkey)
            {
                ShowStatusToast("NO-SAVE ERROR", Palette.Yellow, exception.Message);
            }
            else
            {
                MessageBox.Show(this, exception.Message, "Firewall error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        });
    }

    private void MutateState(bool enabled, bool fromHotkey)
    {
        string? gamePath = null;
        var requestedForegroundWindow = IntPtr.Zero;
        if (enabled)
        {
            if (fromHotkey)
            {
                if (!GameProcessService.TryGetVerifiedForegroundGame(
                        out var foregroundPath, out var liveForegroundWindow) ||
                    !GameProcessService.IsCurrentForegroundWindow(liveForegroundWindow))
                {
                    throw new InvalidOperationException(
                        "GTA V must remain in the foreground to use the shortcut.");
                }
                gamePath = foregroundPath;
                requestedForegroundWindow = liveForegroundWindow;
                _verifiedGamePath = foregroundPath;
                _hotkeyHook.Arm(liveForegroundWindow);
                _controllerShortcutService.Arm(liveForegroundWindow);
            }
            else if (GameProcessService.TryFindVerifiedRunningGame(out var runningPath))
            {
                gamePath = runningPath;
                _verifiedGamePath = runningPath;
            }
            else
            {
                throw new InvalidOperationException(
                    "Start a verified copy of GTA V before enabling no-save.");
            }
        }

        if (!Program.IsRunningAsAdministrator())
        {
            Program.RelaunchElevated(gamePath, requestedForegroundWindow);
            Close();
            return;
        }

        _firewall!.SetNoSaveEnabled(enabled, gamePath);
        SetDisplayedState(enabled);
        if (fromHotkey)
        {
            ShowStatusToast(enabled ? "NO-SAVE ACTIVE" : "NO-SAVE INACTIVE",
                enabled ? Palette.HotPink : Palette.Acid);
        }
    }

    /// <summary>
    /// Runs a firewall mutation with the toggle locked and the refresh loop invalidated, then
    /// reports a failure the way the calling path requires and resynchronizes the display from
    /// the firewall itself — a failed mutation leaves the real state unknown.
    /// </summary>
    private void RunExclusive(Action mutation, Action<Exception> reportFailure)
    {
        if (_applying || _firewall is null)
        {
            return;
        }

        _applying = true;
        Interlocked.Increment(ref _runtimeRefreshVersion);
        _toggle.Enabled = false;
        UseWaitCursor = true;
        try
        {
            mutation();
        }
        catch (Exception exception)
        {
            reportFailure(exception);
            ResynchronizeState();
        }
        finally
        {
            UseWaitCursor = false;
            _toggle.Enabled = _isAdministrator && _stateKnown;
            _applying = false;
        }
    }

    private void ResynchronizeState()
    {
        try
        {
            ApplyFirewallState(_firewall!.GetState());
        }
        catch
        {
            SetUnknownState();
        }
    }

    private void RefreshState(bool showErrors = false)
    {
        if (_applying || _firewall is null)
        {
            return;
        }

        try
        {
            ApplyFirewallState(_firewall.GetState());
        }
        catch (Exception exception)
        {
            SetUnknownState();
            if (showErrors)
            {
                MessageBox.Show(this, exception.Message, "Unable to read Windows Firewall",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void ApplyFirewallState(FirewallRuleState state)
    {
        if (state == FirewallRuleState.Invalid)
        {
            SetInvalidState();
            return;
        }
        SetDisplayedState(state == FirewallRuleState.Active);
    }

    private void SetDisplayedState(bool enabled)
    {
        _stateKnown = true;
        _firewallState = enabled ? FirewallRuleState.Active : FirewallRuleState.Inactive;
        _toggle.IsRecoveryMode = false;
        _toggle.IsStateKnown = true;
        _toggle.Checked = enabled;
        _toggle.Enabled = _isAdministrator && !_applying;
        _miniHud?.SetState(enabled);
        RenderStatus(
            enabled ? Palette.HotPink : Palette.Acid,
            enabled ? "ACTIVE" : "INACTIVE",
            enabled ? "ROCKSTAR LINK BLOCKED" : "ROCKSTAR LINK ONLINE",
            enabled ? "No-save active" : "No-save inactive");
    }

    private void SetInvalidState()
    {
        _stateKnown = true;
        _firewallState = FirewallRuleState.Invalid;
        _toggle.IsStateKnown = false;
        _toggle.IsRecoveryMode = true;
        _toggle.Enabled = _isAdministrator && !_applying;
        _miniHud?.SetState(null);
        RenderStatus(Palette.Yellow, "INVALID", "CLICK RESTORE, THEN RETRY",
            "Restore an invalid VaultLoop firewall rule");
    }

    private void SetUnknownState()
    {
        _stateKnown = false;
        _firewallState = FirewallRuleState.Invalid;
        _toggle.IsRecoveryMode = false;
        _toggle.IsStateKnown = false;
        _toggle.Enabled = false;
        _miniHud?.SetState(null);
        RenderStatus(Palette.Yellow, "UNKNOWN", "FIREWALL STATE UNAVAILABLE",
            "No-save state unknown");
    }

    /// <summary>Paints the status card and its accessible label for one displayed state.</summary>
    private void RenderStatus(
        Color color, string title, string detail, string toggleAccessibleName)
    {
        _stateColor = color;
        _stateKicker.BackColor = color;
        _stateTitle.BackColor = color;
        _stateDetail.BackColor = color;
        _stateTitle.Text = title;
        _stateDetail.Text = detail;
        _toggle.AccessibleName = toggleAccessibleName;
        _trayMenu?.SetStatus(title, color);
        if (_trayIcon is not null)
        {
            _trayIcon.Text = $"VaultLoop - No-save {title.ToLowerInvariant()}";
        }
        Invalidate();
    }

    private static void ShowStatusToast(string title, Color color, string? detail = null)
    {
        var toast = new StatusToastForm(title, detail ?? "", color);
        toast.Show();
    }

    private void HandleClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        _refreshTimer.Stop();
        if (_previewMode || _firewall is null)
        {
            return;
        }

        if (eventArgs.CloseReason == CloseReason.UserClosing &&
            _firewallState == FirewallRuleState.Active)
        {
            var closeAnswer = MessageBox.Show(this,
                "No-save is ACTIVE.\nClosing the application will restore the Rockstar link.\n\nClose now?",
                "No-save active", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (closeAnswer == DialogResult.No)
            {
                eventArgs.Cancel = true;
                _refreshTimer.Start();
                return;
            }
        }

        if (!Program.IsRunningAsAdministrator())
        {
            if (_firewallState != FirewallRuleState.Inactive)
            {
                try
                {
                    Program.RelaunchElevated(null, IntPtr.Zero);
                }
                catch (Exception exception)
                {
                    MessageBox.Show(this,
                        $"The Rockstar link could not be restored:\n{exception.Message}",
                        "Restore failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    eventArgs.Cancel = true;
                    _refreshTimer.Start();
                }
            }
            return;
        }

        try
        {
            _firewall.SetNoSaveEnabled(false);
        }
        catch (Exception exception)
        {
            if (eventArgs.CloseReason != CloseReason.UserClosing)
            {
                return;
            }
            var answer = MessageBox.Show(this,
                $"The Rockstar link could not be restored:\n{exception.Message}\n\nExit anyway?",
                "Restore failed", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.No)
            {
                eventArgs.Cancel = true;
                _refreshTimer.Start();
            }
        }
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

    private sealed class MiniHudForm : Form
    {
        private readonly Label _statusLabel;

        internal MiniHudForm()
        {
            ClientSize = new Size(260, 52);
            BackColor = Color.Black;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            AccessibleName = "VaultLoop no-save status";

            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Font = Typography.DialogTitleBar,
                Text = "No-save Disabled",
                TextAlign = ContentAlignment.MiddleCenter
            };
            Controls.Add(_statusLabel);
        }

        internal void ShowOnActiveScreen()
        {
            var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(workingArea.Right - Width - 24, workingArea.Top + 24);
            Show();
        }

        internal void SetState(bool? enabled)
        {
            _statusLabel.Text = enabled switch
            {
                true => "No-save Enabled",
                false => "No-save Disabled",
                null => "No-save Unknown"
            };
            AccessibleName = _statusLabel.Text;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int ToolWindow = 0x00000080;
                const int Transparent = 0x00000020;
                const int NoActivate = 0x08000000;
                var parameters = base.CreateParams;
                parameters.ExStyle |= ToolWindow | Transparent | NoActivate;
                return parameters;
            }
        }
    }

}
