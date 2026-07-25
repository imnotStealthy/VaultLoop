using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

internal sealed class MainForm : Form
{
    private readonly FirewallService? _firewall;
    private readonly BooleanToggle _toggle;
    private readonly Label _stateKicker;
    private readonly Label _stateTitle;
    private readonly Label _stateDetail;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly bool _previewMode;
    private readonly Image _logoImage;
    private readonly NativeMethods.LowLevelKeyboardProcedure _keyboardProcedure;
    private readonly Button _shortcutBadge;
    private readonly Button _shortcutFooter;
    private readonly Button _themeButton;
    private readonly Label _gameStatusLabel;
    private readonly Dictionary<Control, Color> _originalBackColors = new();
    private readonly Dictionary<Control, Color> _originalForeColors = new();
    private Color _stateColor = Palette.Acid;
    private Keys _shortcutKey;
    private Keys _shortcutModifiers;
    private bool _applying;
    private bool _capturingShortcut;
    private bool _darkMode;
    private bool _stateKnown = true;
    private bool _hotkeyRegistered;
    private bool _shortcutDown;
    private bool _gameHotkeyReady;
    private int _runtimeRefreshInProgress;
    private int _runtimeRefreshVersion;
    private FirewallRuleState _firewallState = FirewallRuleState.Inactive;
    private string? _verifiedGamePath;
    private IntPtr _keyboardHook;
    private long _verifiedGameWindow;

    internal MainForm(FirewallService? firewall, bool previewMode = false,
        bool previewState = false, bool previewUnknown = false)
    {
        _firewall = firewall;
        _previewMode = previewMode;
        _logoImage = LoadLogo();
        _keyboardProcedure = KeyboardHookCallback;
        (_shortcutModifiers, _shortcutKey) = ShortcutSettings.Load();
        _darkMode = ThemeSettings.Load();

        Text = "VaultLoop";
        ClientSize = new Size(780, 520);
        BackColor = Palette.Cream;
        ForeColor = Palette.Ink;
        Font = Typography.Body;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = Size;
        if (!_previewMode)
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }

        var titleBar = BuildTitleBar();
        _shortcutBadge = (Button)titleBar.Controls["ShortcutBadge"]!;
        _themeButton = (Button)titleBar.Controls["ThemeButton"]!;
        Controls.Add(titleBar);

        var headerLogo = new PictureBox
        {
            Bounds = new Rectangle(49, 81, 76, 76),
            Image = _logoImage,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Palette.Yellow,
            TabStop = false
        };
        Controls.Add(headerLogo);
        Controls.Add(MakeLabel("VAULTLOOP / NO-SAVE", new Rectangle(143, 83, 485, 42),
            Typography.ProductTitle, Palette.Yellow));
        Controls.Add(MakeLabel("ROCKSTAR CLOUD CONTROL",
            new Rectangle(145, 126, 480, 22), Typography.MonoCaption, Palette.Yellow));
        var guideButton = MakeActionButton("HOW TO USE", new Rectangle(632, 91, 100, 42), Palette.Ink, Palette.Paper);
        guideButton.AccessibleName = "Open the no-save instruction guide";
        guideButton.Click += (_, _) =>
        {
            using var guide = new GuideDialog(_darkMode);
            guide.ShowDialog(this);
        };
        Controls.Add(guideButton);

        Controls.Add(MakeLabel("NO-SAVE MODE", new Rectangle(55, 222, 310, 32),
            Typography.SectionTitle, Palette.Paper));
        Controls.Add(MakeLabel("Toggle the Rockstar link without cutting the rest of your network.",
            new Rectangle(56, 258, 320, 44), Typography.Body, Palette.Paper));

        _toggle = new BooleanToggle
        {
            Location = new Point(55, 312),
            Size = new Size(315, 86),
            AccessibleName = "Toggle no-save mode",
            AccessibleDescription = "Active blocks the Rockstar link. Inactive restores it."
        };
        _toggle.ToggleRequested += (_, _) => ToggleState();
        Controls.Add(_toggle);

        _stateKicker = MakeLabel("STATUS", new Rectangle(458, 264, 218, 18),
            Typography.CompactMono, Palette.Acid);
        _stateTitle = MakeLabel("", new Rectangle(458, 286, 220, 36),
            Typography.StatusTitle, Palette.Acid);
        _stateDetail = MakeLabel("", new Rectangle(458, 326, 220, 22),
            Typography.StatusDetail, Palette.Acid);
        Controls.AddRange([_stateKicker, _stateTitle, _stateDetail]);

        _shortcutFooter = MakeTextButton($"{ShortcutText}  //  GTA ONLY",
            new Rectangle(44, 454, 370, 34), Typography.MonoCaption,
            Palette.Ink, Palette.Paper);
        _shortcutFooter.AccessibleName = "Configure the GTA-only keyboard shortcut";
        _shortcutFooter.Click += (_, _) => ConfigureShortcut();
        Controls.Add(_shortcutFooter);
        var adminReady = _previewMode || IsRunningAsAdministrator();
        _gameStatusLabel = MakeLabel(
            adminReady ? "WAITING FOR GTA  //  SAFE RESTORE" : "ADMIN REQUIRED",
            new Rectangle(466, 458, 257, 24), Typography.TinyMono, Palette.Ink,
            adminReady ? Palette.Yellow : Palette.HotPink, ContentAlignment.MiddleCenter);
        Controls.Add(_gameStatusLabel);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1200 };
        _refreshTimer.Tick += (_, _) => QueueRuntimeRefresh();
        FormClosing += HandleClosing;
        Shown += HandleShown;
        CaptureThemeColors(this);
        ApplyTheme();

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
            _keyboardHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.LowLevelKeyboardHook, _keyboardProcedure,
                NativeMethods.GetModuleHandle(null), 0);
            _hotkeyRegistered = _keyboardHook != IntPtr.Zero;
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
            _hotkeyRegistered = false;
        }
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            _logoImage.Dispose();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.None;
        DrawCard(e.Graphics, new Rectangle(28, 68, 724, 102), Palette.Yellow, Palette.Ink);
        DrawCard(e.Graphics, new Rectangle(28, 194, 724, 228),
            _darkMode ? Palette.DarkSurface : Palette.Paper, Palette.Blue);
        DrawCard(e.Graphics, new Rectangle(432, 248, 280, 110), _stateColor, Palette.Ink);
        DrawCard(e.Graphics, new Rectangle(28, 446, 724, 48), Palette.Ink, Palette.Blue);
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

    private Panel BuildTitleBar()
    {
        var titleBar = new Panel
        {
            Bounds = new Rectangle(0, 0, ClientSize.Width, 48),
            BackColor = Palette.Ink
        };

        var logo = new PictureBox
        {
            Bounds = new Rectangle(12, 7, 34, 34),
            Image = _logoImage,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Palette.Yellow,
            TabStop = false
        };
        var title = MakeLabel("VAULTLOOP", new Rectangle(58, 7, 280, 34),
            Typography.WindowTitle, Palette.Ink, Palette.Paper);
        var theme = MakeTextButton(_darkMode ? "LIGHT THEME" : "DARK THEME",
            new Rectangle(408, 10, 130, 28), Typography.TinyMono,
            Palette.Blue, Palette.Ink);
        theme.Name = "ThemeButton";
        theme.AccessibleName = _darkMode ? "Switch to light theme" : "Switch to dark theme";
        theme.Click += (_, _) => ToggleTheme();
        var shortcut = MakeTextButton(ShortcutText, new Rectangle(548, 10, 104, 28),
            Typography.CompactMono, Palette.Acid, Palette.Ink);
        shortcut.Name = "ShortcutBadge";
        shortcut.AccessibleName = "Configure keyboard shortcut";
        shortcut.Click += (_, _) => ConfigureShortcut();
        var minimize = MakeWindowButton("-", new Rectangle(684, 0, 48, 48), Palette.Blue);
        var close = MakeWindowButton("X", new Rectangle(732, 0, 48, 48), Palette.HotPink);
        minimize.AccessibleName = "Minimize VaultLoop";
        close.AccessibleName = "Close VaultLoop";

        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        close.Click += (_, _) => Close();
        titleBar.MouseDown += BeginWindowDrag;
        logo.MouseDown += BeginWindowDrag;
        title.MouseDown += BeginWindowDrag;

        titleBar.Controls.AddRange([logo, title, theme, shortcut, minimize, close]);
        return titleBar;
    }

    private static Button MakeWindowButton(string text, Rectangle bounds, Color hoverColor)
    {
        var button = new Button
        {
            Text = text,
            Bounds = bounds,
            BackColor = Palette.Ink,
            ForeColor = Palette.Paper,
            FlatStyle = FlatStyle.Flat,
            Font = Typography.WindowTitle,
            TabStop = true,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = hoverColor;
        button.FlatAppearance.MouseDownBackColor = hoverColor;
        button.MouseEnter += (_, _) => button.ForeColor = Palette.Ink;
        button.MouseLeave += (_, _) => button.ForeColor = Palette.Paper;
        return button;
    }

    private static Button MakeTextButton(string text, Rectangle bounds, Font font,
        Color backColor, Color foreColor)
    {
        var button = new Button
        {
            Text = text,
            Bounds = bounds,
            Font = font,
            BackColor = backColor,
            ForeColor = foreColor,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            TextAlign = ContentAlignment.MiddleCenter
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = backColor;
        button.FlatAppearance.MouseDownBackColor = backColor;
        return button;
    }

    private static Button MakeActionButton(string text, Rectangle bounds, Color backColor,
        Color foreColor)
    {
        var button = new Button
        {
            Text = text,
            Bounds = bounds,
            BackColor = backColor,
            ForeColor = foreColor,
            FlatStyle = FlatStyle.Flat,
            Font = Typography.ActionButton,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = Palette.Ink;
        button.FlatAppearance.BorderSize = 3;
        button.FlatAppearance.MouseOverBackColor = Palette.Blue;
        var originalForeColor = foreColor;
        button.MouseEnter += (_, _) => button.ForeColor = Palette.Ink;
        button.MouseLeave += (_, _) => button.ForeColor = originalForeColor;
        return button;
    }

    private void BeginWindowDrag(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left)
        {
            return;
        }
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(Handle, NativeMethods.NonClientLeftButtonDown,
            NativeMethods.HitCaption, 0);
    }

    private void HandleShown(object? sender, EventArgs eventArgs)
    {
        if (!_previewMode && !_hotkeyRegistered)
        {
            MessageBox.Show(this,
                $"The {ShortcutText} keyboard hook could not be installed.\n" +
                "The on-screen toggle remains available.",
                "Shortcut unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wordParameter, IntPtr longParameter)
    {
        if (code >= 0)
        {
            var keyboardData =
                Marshal.PtrToStructure<NativeMethods.LowLevelKeyboardData>(longParameter);
            if ((keyboardData.Flags &
                 (NativeMethods.InjectedFlag | NativeMethods.LowerIntegrityInjectedFlag)) != 0)
            {
                return NativeMethods.CallNextHookEx(
                    _keyboardHook, code, wordParameter, longParameter);
            }
            if (!_capturingShortcut && keyboardData.VirtualKeyCode == (uint)_shortcutKey)
            {
                var message = wordParameter.ToInt32();
                var keyDown = message is
                    NativeMethods.KeyDownMessage or NativeMethods.SystemKeyDownMessage;
                var keyUp = message is
                    NativeMethods.KeyUpMessage or NativeMethods.SystemKeyUpMessage;
                var pressedModifiers = GetPressedModifiers(keyboardData.Flags);
                var modifiersMatch = pressedModifiers == _shortcutModifiers;

                var canTrigger = keyDown && modifiersMatch &&
                                  Volatile.Read(ref _gameHotkeyReady) &&
                                  GameProcessService.IsCurrentForegroundWindow(
                                      new IntPtr(Interlocked.Read(ref _verifiedGameWindow))) &&
                                  !_applying && _stateKnown;
                if (canTrigger || (keyUp && _shortcutDown))
                {
                    if (keyDown && !_shortcutDown)
                    {
                        _shortcutDown = true;
                        if (!IsDisposed && IsHandleCreated)
                        {
                            BeginInvoke(new Action(() => ToggleState(fromHotkey: true)));
                        }
                    }
                    else if (keyUp)
                    {
                        _shortcutDown = false;
                    }
                    return (IntPtr)1;
                }
            }
        }
        return NativeMethods.CallNextHookEx(
            _keyboardHook, code, wordParameter, longParameter);
    }

    private Keys GetPressedModifiers(uint flags)
    {
        var modifiers = Keys.None;
        if ((flags & NativeMethods.AltDownFlag) != 0)
        {
            modifiers |= Keys.Alt;
        }
        if ((NativeMethods.GetAsyncKeyState((int)Keys.ControlKey) & 0x8000) != 0)
        {
            modifiers |= Keys.Control;
        }
        if ((NativeMethods.GetAsyncKeyState((int)Keys.ShiftKey) & 0x8000) != 0)
        {
            modifiers |= Keys.Shift;
        }
        return modifiers;
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
        Volatile.Write(ref _gameHotkeyReady, false);
        Interlocked.Exchange(ref _verifiedGameWindow, 0);
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
        return snapshot;
    }

    private void ApplyRuntimeSnapshot(RuntimeSnapshot snapshot, int version)
    {
        if (version != Volatile.Read(ref _runtimeRefreshVersion) || _applying)
        {
            return;
        }

        if (snapshot.ForegroundPath is not null)
        {
            _verifiedGamePath = snapshot.ForegroundPath;
            Interlocked.Exchange(ref _verifiedGameWindow, snapshot.ForegroundWindow.ToInt64());
            Volatile.Write(ref _gameHotkeyReady, true);
            _gameStatusLabel.Text = "GTA READY  //  SAFE RESTORE";
            _gameStatusLabel.BackColor = Palette.Acid;
        }
        else
        {
            Volatile.Write(ref _gameHotkeyReady, false);
            Interlocked.Exchange(ref _verifiedGameWindow, 0);
            _verifiedGamePath = snapshot.RunningPath;
            _gameStatusLabel.Text = snapshot.RunningPath is null
                ? "WAITING FOR GTA"
                : "GTA IN BACKGROUND";
            _gameStatusLabel.BackColor = Palette.Yellow;
        }

        if (snapshot.FirewallState.HasValue)
        {
            ApplyFirewallState(snapshot.FirewallState.Value);
        }
        else if (snapshot.FirewallError is not null)
        {
            SetUnknownState();
        }
    }

    private void RefreshGameContext()
    {
        Volatile.Write(ref _gameHotkeyReady, false);
        Interlocked.Exchange(ref _verifiedGameWindow, 0);
        if (GameProcessService.TryGetVerifiedForegroundGame(
                out var foregroundPath, out var foregroundWindow))
        {
            _verifiedGamePath = foregroundPath;
            Interlocked.Exchange(ref _verifiedGameWindow, foregroundWindow.ToInt64());
            Volatile.Write(ref _gameHotkeyReady, true);
            _gameStatusLabel.Text = "GTA READY  //  SAFE RESTORE";
            _gameStatusLabel.BackColor = Palette.Acid;
            return;
        }

        if (GameProcessService.TryFindVerifiedRunningGame(out var runningPath))
        {
            _verifiedGamePath = runningPath;
            _gameStatusLabel.Text = "GTA IN BACKGROUND";
            _gameStatusLabel.BackColor = Palette.Yellow;
        }
        else
        {
            _verifiedGamePath = null;
            _gameStatusLabel.Text = "WAITING FOR GTA";
            _gameStatusLabel.BackColor = Palette.Yellow;
        }
    }

    private string ShortcutText => ShortcutSettings.Format(_shortcutModifiers, _shortcutKey);

    private void ConfigureShortcut()
    {
        _capturingShortcut = true;
        try
        {
            using var dialog = new ShortcutDialog(_shortcutModifiers, _shortcutKey, _darkMode);
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var newModifiers = dialog.ShortcutModifiers;
            var newKey = dialog.ShortcutKey;
            ShortcutSettings.Save(newModifiers, newKey);
            _shortcutModifiers = newModifiers;
            _shortcutKey = newKey;
            _shortcutDown = false;
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
            _capturingShortcut = false;
        }
    }

    private void ToggleTheme()
    {
        _darkMode = !_darkMode;
        ApplyTheme();
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

    private void CaptureThemeColors(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (!ReferenceEquals(control, _stateKicker) &&
                !ReferenceEquals(control, _stateTitle) &&
                !ReferenceEquals(control, _stateDetail) &&
                !ReferenceEquals(control, _toggle))
            {
                _originalBackColors[control] = control.BackColor;
                _originalForeColors[control] = control.ForeColor;
            }
            CaptureThemeColors(control);
        }
    }

    private void ApplyTheme()
    {
        BackColor = _darkMode ? Palette.DarkCanvas : Palette.Cream;
        ForeColor = _darkMode ? Palette.Paper : Palette.Ink;
        foreach (var entry in _originalBackColors)
        {
            var originalBack = entry.Value;
            var mappedBack = originalBack == Palette.Cream
                ? (_darkMode ? Palette.DarkCanvas : Palette.Cream)
                : originalBack == Palette.Paper
                    ? (_darkMode ? Palette.DarkSurface : Palette.Paper)
                    : originalBack;
            entry.Key.BackColor = mappedBack;

            var originalFore = _originalForeColors[entry.Key];
            entry.Key.ForeColor = _darkMode && originalFore == Palette.Ink &&
                                  (originalBack == Palette.Cream || originalBack == Palette.Paper)
                ? Palette.Paper
                : originalFore;
        }
        _themeButton.Text = _darkMode ? "LIGHT THEME" : "DARK THEME";
        _themeButton.AccessibleName = _darkMode
            ? "Switch to light theme"
            : "Switch to dark theme";
        Invalidate(true);
    }

    private void ToggleState(bool fromHotkey = false)
    {
        if (!_stateKnown)
        {
            return;
        }
        ApplyState(_firewallState == FirewallRuleState.Inactive, fromHotkey);
    }

    private void ApplyState(bool enabled, bool fromHotkey = false)
    {
        if (_applying || _firewall is null || !_stateKnown)
        {
            return;
        }

        _applying = true;
        Interlocked.Increment(ref _runtimeRefreshVersion);
        _toggle.Enabled = false;
        UseWaitCursor = true;
        try
        {
            string? gamePath = null;
            if (enabled)
            {
                if (fromHotkey)
                {
                    if (!GameProcessService.TryGetVerifiedForegroundGame(
                            out var foregroundPath, out var foregroundWindow) ||
                        !GameProcessService.IsCurrentForegroundWindow(foregroundWindow))
                    {
                        throw new InvalidOperationException(
                            "GTA V must remain in the foreground to use the shortcut.");
                    }
                    gamePath = foregroundPath;
                    _verifiedGamePath = foregroundPath;
                    Interlocked.Exchange(
                        ref _verifiedGameWindow, foregroundWindow.ToInt64());
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

            _firewall.SetNoSaveEnabled(enabled, gamePath);
            SetDisplayedState(enabled);
            if (fromHotkey)
            {
                ShowStatusToast(enabled ? "NO-SAVE ACTIVE" : "NO-SAVE INACTIVE",
                    enabled ? Palette.HotPink : Palette.Acid);
            }
        }
        catch (Exception exception)
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
            try
            {
                ApplyFirewallState(_firewall.GetState());
            }
            catch
            {
                SetUnknownState();
            }
        }
        finally
        {
            UseWaitCursor = false;
            _toggle.Enabled = _stateKnown;
            _applying = false;
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
        _toggle.Enabled = !_applying;
        _toggle.AccessibleName = enabled ? "No-save active" : "No-save inactive";
        _stateColor = enabled ? Palette.HotPink : Palette.Acid;
        _stateKicker.BackColor = _stateColor;
        _stateTitle.BackColor = _stateColor;
        _stateDetail.BackColor = _stateColor;
        _stateTitle.Text = enabled ? "ACTIVE" : "INACTIVE";
        _stateDetail.Text = enabled ? "ROCKSTAR LINK BLOCKED" : "ROCKSTAR LINK ONLINE";
        Invalidate();
    }

    private void SetInvalidState()
    {
        _stateKnown = true;
        _firewallState = FirewallRuleState.Invalid;
        _toggle.IsStateKnown = false;
        _toggle.IsRecoveryMode = true;
        _toggle.Enabled = !_applying;
        _stateColor = Palette.Yellow;
        _stateKicker.BackColor = _stateColor;
        _stateTitle.BackColor = _stateColor;
        _stateDetail.BackColor = _stateColor;
        _stateTitle.Text = "INVALID";
        _stateDetail.Text = "CLICK RESTORE, THEN RETRY";
        _toggle.AccessibleName = "Restore an invalid VaultLoop firewall rule";
        Invalidate();
    }

    private void SetUnknownState()
    {
        _stateKnown = false;
        _firewallState = FirewallRuleState.Invalid;
        _toggle.IsRecoveryMode = false;
        _toggle.IsStateKnown = false;
        _toggle.Enabled = false;
        _stateColor = Palette.Yellow;
        _stateKicker.BackColor = _stateColor;
        _stateTitle.BackColor = _stateColor;
        _stateDetail.BackColor = _stateColor;
        _stateTitle.Text = "UNKNOWN";
        _stateDetail.Text = "FIREWALL STATE UNAVAILABLE";
        _toggle.AccessibleName = "No-save state unknown";
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

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static Image LoadLogo()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("ReplayGlitchLogo.png")
            ?? throw new InvalidOperationException("Embedded logo resource not found.");
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    private static Label MakeLabel(string text, Rectangle bounds, Font font, Color backColor,
        Color? foreColor = null, ContentAlignment alignment = ContentAlignment.MiddleLeft) =>
        new()
        {
            Text = text,
            Bounds = bounds,
            Font = font,
            BackColor = backColor,
            ForeColor = foreColor ?? Palette.Ink,
            TextAlign = alignment,
            AutoEllipsis = true
        };

    private static void DrawCard(Graphics graphics, Rectangle bounds, Color fill, Color shadow)
    {
        using var shadowBrush = new SolidBrush(shadow);
        using var fillBrush = new SolidBrush(fill);
        using var borderPen = new Pen(Palette.Ink, 4F);
        graphics.FillRectangle(shadowBrush,
            new Rectangle(bounds.X + 8, bounds.Y + 8, bounds.Width, bounds.Height));
        graphics.FillRectangle(fillBrush, bounds);
        graphics.DrawRectangle(borderPen, bounds);
    }

    private sealed class RuntimeSnapshot
    {
        internal string? ForegroundPath { get; set; }
        internal IntPtr ForegroundWindow { get; set; }
        internal string? RunningPath { get; set; }
        internal FirewallRuleState? FirewallState { get; set; }
        internal Exception? FirewallError { get; set; }
    }

}
