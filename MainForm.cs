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
    private static readonly Color Ink = Color.FromArgb(17, 17, 17);
    private static readonly Color Cream = Color.FromArgb(255, 246, 218);
    private static readonly Color Paper = Color.FromArgb(255, 253, 245);
    private static readonly Color Yellow = Color.FromArgb(255, 215, 56);
    private static readonly Color Blue = Color.FromArgb(91, 134, 255);
    private static readonly Color Acid = Color.FromArgb(185, 255, 61);
    private static readonly Color HotPink = Color.FromArgb(255, 83, 112);
    private static readonly Color DarkCanvas = Color.FromArgb(20, 20, 20);
    private static readonly Color DarkSurface = Color.FromArgb(34, 34, 34);

    private const int LowLevelKeyboardHook = 13;
    private const int KeyDownMessage = 0x0100;
    private const int KeyUpMessage = 0x0101;
    private const int SystemKeyDownMessage = 0x0104;
    private const int SystemKeyUpMessage = 0x0105;
    private const uint LowerIntegrityInjectedFlag = 0x02;
    private const uint InjectedFlag = 0x10;
    private const uint AltDownFlag = 0x20;
    private const int NonClientLeftButtonDown = 0x00A1;
    private const int HitCaption = 0x0002;

    private readonly FirewallService? _firewall;
    private readonly BooleanToggle _toggle;
    private readonly Label _stateKicker;
    private readonly Label _stateTitle;
    private readonly Label _stateDetail;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly bool _previewMode;
    private readonly Image _logoImage;
    private readonly LowLevelKeyboardProcedure _keyboardProcedure;
    private readonly Button _shortcutBadge;
    private readonly Button _shortcutFooter;
    private readonly Button _themeButton;
    private readonly Label _gameStatusLabel;
    private readonly Dictionary<Control, Color> _originalBackColors = new();
    private readonly Dictionary<Control, Color> _originalForeColors = new();
    private Color _stateColor = Acid;
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
        BackColor = Cream;
        ForeColor = Ink;
        Font = new Font("Bahnschrift", 10F, FontStyle.Regular);
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
            BackColor = Yellow,
            TabStop = false
        };
        Controls.Add(headerLogo);
        Controls.Add(MakeLabel("VAULTLOOP / NO-SAVE", new Rectangle(143, 83, 485, 42),
            new Font("Impact", 26F), Yellow));
        Controls.Add(MakeLabel("ROCKSTAR CLOUD CONTROL",
            new Rectangle(145, 126, 480, 22), new Font("Consolas", 10F, FontStyle.Bold), Yellow));
        var guideButton = MakeActionButton("HOW TO USE", new Rectangle(632, 91, 100, 42), Ink, Paper);
        guideButton.AccessibleName = "Open the no-save instruction guide";
        guideButton.Click += (_, _) =>
        {
            using var guide = new GuideDialog(_darkMode);
            guide.ShowDialog(this);
        };
        Controls.Add(guideButton);

        Controls.Add(MakeLabel("NO-SAVE MODE", new Rectangle(55, 222, 310, 32),
            new Font("Bahnschrift", 18F, FontStyle.Bold), Paper));
        Controls.Add(MakeLabel("Toggle the Rockstar link without cutting the rest of your network.",
            new Rectangle(56, 258, 320, 44), new Font("Bahnschrift", 10F), Paper));

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
            new Font("Consolas", 9F, FontStyle.Bold), Acid);
        _stateTitle = MakeLabel("", new Rectangle(458, 286, 220, 36),
            new Font("Impact", 23F), Acid);
        _stateDetail = MakeLabel("", new Rectangle(458, 326, 220, 22),
            new Font("Bahnschrift", 9F, FontStyle.Bold), Acid);
        Controls.AddRange([_stateKicker, _stateTitle, _stateDetail]);

        _shortcutFooter = MakeTextButton($"{ShortcutText}  //  GTA ONLY",
            new Rectangle(44, 454, 370, 34), new Font("Consolas", 10F, FontStyle.Bold),
            Ink, Paper);
        _shortcutFooter.AccessibleName = "Configure the GTA-only keyboard shortcut";
        _shortcutFooter.Click += (_, _) => ConfigureShortcut();
        Controls.Add(_shortcutFooter);
        var adminReady = _previewMode || IsRunningAsAdministrator();
        _gameStatusLabel = MakeLabel(
            adminReady ? "WAITING FOR GTA  //  SAFE RESTORE" : "ADMIN REQUIRED",
            new Rectangle(466, 458, 257, 24), new Font("Consolas", 8.5F, FontStyle.Bold), Ink,
            adminReady ? Yellow : HotPink, ContentAlignment.MiddleCenter);
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
            _keyboardHook = SetWindowsHookEx(LowLevelKeyboardHook, _keyboardProcedure,
                GetModuleHandle(null), 0);
            _hotkeyRegistered = _keyboardHook != IntPtr.Zero;
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
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
        DrawCard(e.Graphics, new Rectangle(28, 68, 724, 102), Yellow, Ink);
        DrawCard(e.Graphics, new Rectangle(28, 194, 724, 228),
            _darkMode ? DarkSurface : Paper, Blue);
        DrawCard(e.Graphics, new Rectangle(432, 248, 280, 110), _stateColor, Ink);
        DrawCard(e.Graphics, new Rectangle(28, 446, 724, 48), Ink, Blue);
        using var borderPen = new Pen(Ink, 3F);
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
            BackColor = Ink
        };

        var logo = new PictureBox
        {
            Bounds = new Rectangle(12, 7, 34, 34),
            Image = _logoImage,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Yellow,
            TabStop = false
        };
        var title = MakeLabel("VAULTLOOP", new Rectangle(58, 7, 280, 34),
            new Font("Bahnschrift", 11F, FontStyle.Bold), Ink, Paper);
        var theme = MakeTextButton(_darkMode ? "LIGHT THEME" : "DARK THEME",
            new Rectangle(408, 10, 130, 28), new Font("Consolas", 8.5F, FontStyle.Bold),
            Blue, Ink);
        theme.Name = "ThemeButton";
        theme.AccessibleName = _darkMode ? "Switch to light theme" : "Switch to dark theme";
        theme.Click += (_, _) => ToggleTheme();
        var shortcut = MakeTextButton(ShortcutText, new Rectangle(548, 10, 104, 28),
            new Font("Consolas", 9F, FontStyle.Bold), Acid, Ink);
        shortcut.Name = "ShortcutBadge";
        shortcut.AccessibleName = "Configure keyboard shortcut";
        shortcut.Click += (_, _) => ConfigureShortcut();
        var minimize = MakeWindowButton("-", new Rectangle(684, 0, 48, 48), Blue);
        var close = MakeWindowButton("X", new Rectangle(732, 0, 48, 48), HotPink);
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
            BackColor = Ink,
            ForeColor = Paper,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Bahnschrift", 11F, FontStyle.Bold),
            TabStop = true,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = hoverColor;
        button.FlatAppearance.MouseDownBackColor = hoverColor;
        button.MouseEnter += (_, _) => button.ForeColor = Ink;
        button.MouseLeave += (_, _) => button.ForeColor = Paper;
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
            Font = new Font("Bahnschrift", 8F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = Ink;
        button.FlatAppearance.BorderSize = 3;
        button.FlatAppearance.MouseOverBackColor = Blue;
        var originalForeColor = foreColor;
        button.MouseEnter += (_, _) => button.ForeColor = Ink;
        button.MouseLeave += (_, _) => button.ForeColor = originalForeColor;
        return button;
    }

    private void BeginWindowDrag(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left)
        {
            return;
        }
        ReleaseCapture();
        SendMessage(Handle, NonClientLeftButtonDown, HitCaption, 0);
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
            var keyboardData = Marshal.PtrToStructure<LowLevelKeyboardData>(longParameter);
            if ((keyboardData.Flags & (InjectedFlag | LowerIntegrityInjectedFlag)) != 0)
            {
                return CallNextHookEx(_keyboardHook, code, wordParameter, longParameter);
            }
            if (!_capturingShortcut && keyboardData.VirtualKeyCode == (uint)_shortcutKey)
            {
                var message = wordParameter.ToInt32();
                var keyDown = message is KeyDownMessage or SystemKeyDownMessage;
                var keyUp = message is KeyUpMessage or SystemKeyUpMessage;
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
        return CallNextHookEx(_keyboardHook, code, wordParameter, longParameter);
    }

    private Keys GetPressedModifiers(uint flags)
    {
        var modifiers = Keys.None;
        if ((flags & AltDownFlag) != 0)
        {
            modifiers |= Keys.Alt;
        }
        if ((GetAsyncKeyState((int)Keys.ControlKey) & 0x8000) != 0)
        {
            modifiers |= Keys.Control;
        }
        if ((GetAsyncKeyState((int)Keys.ShiftKey) & 0x8000) != 0)
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
            _gameStatusLabel.BackColor = Acid;
        }
        else
        {
            Volatile.Write(ref _gameHotkeyReady, false);
            Interlocked.Exchange(ref _verifiedGameWindow, 0);
            _verifiedGamePath = snapshot.RunningPath;
            _gameStatusLabel.Text = snapshot.RunningPath is null
                ? "WAITING FOR GTA"
                : "GTA IN BACKGROUND";
            _gameStatusLabel.BackColor = Yellow;
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
            _gameStatusLabel.BackColor = Acid;
            return;
        }

        if (GameProcessService.TryFindVerifiedRunningGame(out var runningPath))
        {
            _verifiedGamePath = runningPath;
            _gameStatusLabel.Text = "GTA IN BACKGROUND";
            _gameStatusLabel.BackColor = Yellow;
        }
        else
        {
            _verifiedGamePath = null;
            _gameStatusLabel.Text = "WAITING FOR GTA";
            _gameStatusLabel.BackColor = Yellow;
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
        BackColor = _darkMode ? DarkCanvas : Cream;
        ForeColor = _darkMode ? Paper : Ink;
        foreach (var entry in _originalBackColors)
        {
            var originalBack = entry.Value;
            var mappedBack = originalBack == Cream
                ? (_darkMode ? DarkCanvas : Cream)
                : originalBack == Paper
                    ? (_darkMode ? DarkSurface : Paper)
                    : originalBack;
            entry.Key.BackColor = mappedBack;

            var originalFore = _originalForeColors[entry.Key];
            entry.Key.ForeColor = _darkMode && originalFore == Ink &&
                                  (originalBack == Cream || originalBack == Paper)
                ? Paper
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
                    enabled ? HotPink : Acid);
            }
        }
        catch (Exception exception)
        {
            if (fromHotkey)
            {
                ShowStatusToast("NO-SAVE ERROR", Yellow, exception.Message);
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
        _stateColor = enabled ? HotPink : Acid;
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
        _stateColor = Yellow;
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
        _stateColor = Yellow;
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
            ForeColor = foreColor ?? Ink,
            TextAlign = alignment,
            AutoEllipsis = true
        };

    private static void DrawCard(Graphics graphics, Rectangle bounds, Color fill, Color shadow)
    {
        using var shadowBrush = new SolidBrush(shadow);
        using var fillBrush = new SolidBrush(fill);
        using var borderPen = new Pen(Ink, 4F);
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

    private delegate IntPtr LowLevelKeyboardProcedure(int code, IntPtr wordParameter,
        IntPtr longParameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelKeyboardProcedure procedure,
        IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hookHandle, int code, IntPtr wordParameter,
        IntPtr longParameter);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr windowHandle, int message, int wordParameter,
        int longParameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardData
    {
        internal uint VirtualKeyCode;
        internal uint ScanCode;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }
}

internal static class ShortcutSettings
{
    private const Keys DefaultModifiers = Keys.Control | Keys.Shift;
    private const Keys DefaultKey = Keys.F8;

    internal static (Keys Modifiers, Keys Key) Default => (DefaultModifiers, DefaultKey);

    internal static (Keys Modifiers, Keys Key) Load()
    {
        try
        {
            var rawValue = AppSettingsStorage.ReadText(
                "shortcut.txt", includeLegacy: true, out var fromLegacy);
            if (rawValue is null)
            {
                return (DefaultModifiers, DefaultKey);
            }

            var parts = rawValue.Split('|');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out var modifiersValue) &&
                int.TryParse(parts[1], out var keyValue))
            {
                var modifiers = (Keys)modifiersValue & Keys.Modifiers;
                var key = (Keys)keyValue & Keys.KeyCode;
                if (ShortcutDialog.IsValidShortcut(modifiers, key))
                {
                    if (modifiers == Keys.Alt && key == Keys.F8)
                    {
                        TryMigrate(DefaultModifiers, DefaultKey, fromLegacy);
                        return (DefaultModifiers, DefaultKey);
                    }
                    TryMigrate(modifiers, key, fromLegacy);
                    return (modifiers, key);
                }
            }
        }
        catch
        {
            // A malformed or inaccessible setting must not prevent the app from starting.
        }
        return (DefaultModifiers, DefaultKey);
    }

    internal static void Save(Keys modifiers, Keys key)
    {
        AppSettingsStorage.WriteText("shortcut.txt", $"{(int)modifiers}|{(int)key}");
    }

    private static void TryMigrate(Keys modifiers, Keys key, bool fromLegacy)
    {
        if (!fromLegacy)
        {
            return;
        }
        try
        {
            Save(modifiers, key);
        }
        catch
        {
            // A valid legacy preference remains usable even if migration cannot be persisted.
        }
    }

    internal static string Format(Keys modifiers, Keys key)
    {
        var parts = new List<string>();
        if ((modifiers & Keys.Control) != 0) parts.Add("CTRL");
        if ((modifiers & Keys.Alt) != 0) parts.Add("ALT");
        if ((modifiers & Keys.Shift) != 0) parts.Add("SHIFT");
        parts.Add(FormatKey(key));
        return string.Join("+", parts);
    }

    private static string FormatKey(Keys key)
    {
        if (key is >= Keys.D0 and <= Keys.D9)
        {
            return ((int)key - (int)Keys.D0).ToString();
        }
        if (key is >= Keys.NumPad0 and <= Keys.NumPad9)
        {
            return $"NUM {(int)key - (int)Keys.NumPad0}";
        }
        return key.ToString().ToUpperInvariant();
    }
}

internal static class ThemeSettings
{
    internal static bool Load()
    {
        try
        {
            var rawValue = AppSettingsStorage.ReadText(
                "theme.txt", includeLegacy: true, out var fromLegacy);
            var darkMode = rawValue?.Trim()
                .Equals("dark", StringComparison.OrdinalIgnoreCase) == true;
            if (fromLegacy)
            {
                try
                {
                    Save(darkMode);
                }
                catch
                {
                }
            }
            return darkMode;
        }
        catch
        {
            return false;
        }
    }

    internal static void Save(bool darkMode)
    {
        AppSettingsStorage.WriteText("theme.txt", darkMode ? "dark" : "light");
    }
}

internal static class GuideProgressSettings
{
    internal static int Load()
    {
        try
        {
            var value = AppSettingsStorage.ReadText(
                "guide-step.txt", includeLegacy: false, out _);
            return int.TryParse(value, out var step) && step is >= 1 and <= 6 ? step : 1;
        }
        catch
        {
            return 1;
        }
    }

    internal static void Save(int step)
    {
        if (step is < 1 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(step));
        }
        AppSettingsStorage.WriteText("guide-step.txt", step.ToString());
    }
}

internal abstract class BrutalistDialog : Form
{
    protected static readonly Color Ink = Color.FromArgb(17, 17, 17);
    protected static readonly Color Paper = Color.FromArgb(255, 253, 245);
    protected static readonly Color Yellow = Color.FromArgb(255, 215, 56);
    protected static readonly Color Acid = Color.FromArgb(185, 255, 61);
    protected static readonly Color Blue = Color.FromArgb(91, 134, 255);
    protected static readonly Color AlertRed = Color.FromArgb(232, 54, 70);
    protected static readonly Color DarkCanvas = Color.FromArgb(20, 20, 20);
    protected static readonly Color DarkSurface = Color.FromArgb(34, 34, 34);

    protected BrutalistDialog(string title, Size size, Color background)
    {
        Text = title;
        ClientSize = size;
        BackColor = background;
        ForeColor = background == DarkCanvas ? Paper : Ink;
        Font = new Font("Bahnschrift", 10F);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        var titleBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Ink
        };
        var titleLabel = new Label
        {
            Text = title,
            Bounds = new Rectangle(16, 0, size.Width - 68, 44),
            BackColor = Ink,
            ForeColor = Paper,
            Font = new Font("Bahnschrift", 10F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var closeButton = CreateButton("X", new Rectangle(size.Width - 48, 0, 48, 44),
            Ink, Paper);
        closeButton.AccessibleName = "Close dialog";
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.FlatAppearance.MouseOverBackColor = AlertRed;
        closeButton.MouseEnter += (_, _) => closeButton.ForeColor = Ink;
        closeButton.MouseLeave += (_, _) => closeButton.ForeColor = Paper;
        closeButton.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        titleBar.MouseDown += BeginDrag;
        titleLabel.MouseDown += BeginDrag;
        titleBar.Controls.AddRange([titleLabel, closeButton]);
        Controls.Add(titleBar);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Ink, 3F);
        e.Graphics.DrawRectangle(pen, 1, 1, ClientSize.Width - 3, ClientSize.Height - 3);
    }

    protected static Button CreateButton(string text, Rectangle bounds, Color backColor,
        Color foreColor)
    {
        var button = new Button
        {
            Text = text,
            Bounds = bounds,
            BackColor = backColor,
            ForeColor = foreColor,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Bahnschrift", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = Ink;
        button.FlatAppearance.BorderSize = 3;
        return button;
    }

    private void BeginDrag(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left)
        {
            return;
        }
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, 0x0002, 0);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr windowHandle, int message, int wordParameter,
        int longParameter);
}

internal sealed class ShortcutDialog : BrutalistDialog
{
    private readonly Button _capturedButton;

    internal Keys ShortcutModifiers { get; private set; }
    internal Keys ShortcutKey { get; private set; }

    internal ShortcutDialog(Keys modifiers, Keys key, bool darkMode) :
        base("CONFIGURE SHORTCUT", new Size(430, 280),
            darkMode ? DarkCanvas : Yellow)
    {
        ShortcutModifiers = modifiers;
        ShortcutKey = key;
        KeyPreview = false;
        var canvas = darkMode ? DarkCanvas : Yellow;
        var textColor = darkMode ? Paper : Ink;

        Controls.Add(new Label
        {
            Text = "KEYBOARD SHORTCUT",
            Bounds = new Rectangle(28, 60, 360, 34),
            Font = new Font("Impact", 20F),
            BackColor = canvas,
            ForeColor = textColor
        });
        Controls.Add(new Label
        {
            Text = "Press a new combination. Use a modifier, or a function key.",
            Bounds = new Rectangle(30, 100, 370, 38),
            BackColor = canvas,
            ForeColor = textColor
        });

        _capturedButton = CreateButton(ShortcutSettings.Format(modifiers, key),
            new Rectangle(30, 144, 370, 52),
            darkMode ? DarkSurface : Paper,
            darkMode ? Paper : Ink);
        _capturedButton.Name = "ShortcutCapture";
        _capturedButton.AccessibleName = "Keyboard shortcut capture field";
        _capturedButton.AccessibleDescription =
            "Focus this control and press the shortcut you want to use.";
        _capturedButton.Font = new Font("Consolas", 16F, FontStyle.Bold);
        _capturedButton.KeyDown += CaptureShortcut;
        Controls.Add(_capturedButton);

        var secondaryColor = darkMode ? DarkSurface : Paper;
        var secondaryText = darkMode ? Paper : Ink;
        var resetButton = CreateButton("RESET", new Rectangle(30, 218, 86, 36),
            secondaryColor, secondaryText);
        var saveButton = CreateButton("SAVE", new Rectangle(220, 218, 84, 36), Acid, Ink);
        var cancelButton = CreateButton("CANCEL", new Rectangle(314, 218, 86, 36),
            secondaryColor, secondaryText);
        resetButton.Click += (_, _) =>
        {
            (ShortcutModifiers, ShortcutKey) = ShortcutSettings.Default;
            _capturedButton.Text = ShortcutSettings.Format(ShortcutModifiers, ShortcutKey);
        };
        saveButton.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.AddRange([resetButton, saveButton, cancelButton]);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Shown += (_, _) => _capturedButton.Focus();
    }

    internal static bool IsValidShortcut(Keys modifiers, Keys key)
    {
        var isFunctionKey = key >= Keys.F1 && key <= Keys.F24;
        var hasModifier = (modifiers & (Keys.Control | Keys.Alt | Keys.Shift)) != Keys.None;
        return key != Keys.None && (isFunctionKey || hasModifier) &&
               !(key == Keys.F4 && modifiers == Keys.Alt) &&
               !(key == Keys.Tab && modifiers == Keys.Alt);
    }

    private void CaptureShortcut(object? sender, KeyEventArgs eventArgs)
    {
        var key = eventArgs.KeyCode;
        if (key is Keys.Tab or Keys.Escape or Keys.Enter or Keys.Space or
            Keys.Left or Keys.Right or Keys.Up or Keys.Down or
            Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown)
        {
            return;
        }
        if (key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey)
        {
            return;
        }

        var modifiers = eventArgs.Modifiers & (Keys.Control | Keys.Alt | Keys.Shift);
        if (!IsValidShortcut(modifiers, key))
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        ShortcutModifiers = modifiers;
        ShortcutKey = key;
        _capturedButton.Text = ShortcutSettings.Format(modifiers, key);
        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
    }
}

internal sealed class GuideDialog : BrutalistDialog
{
    private readonly GuideStepPanel[] _steps = new GuideStepPanel[6];
    private readonly Label _currentStepLabel;
    private readonly bool _darkMode;

    internal GuideDialog(bool darkMode) :
        base("HOW TO USE NO-SAVE", GetGuideSize(), darkMode ? DarkCanvas : Paper)
    {
        _darkMode = darkMode;
        AutoScroll = true;
        AutoScrollMinSize = new Size(0, 700);
        var canvas = darkMode ? DarkCanvas : Paper;
        var textColor = darkMode ? Paper : Ink;
        Controls.Add(new Label
        {
            Text = "VAULTLOOP WORKFLOW",
            Bounds = new Rectangle(28, 58, 350, 38),
            BackColor = canvas,
            ForeColor = textColor,
            Font = new Font("Impact", 22F)
        });
        Controls.Add(new Label
        {
            Text = "Click a step to mark your current position.",
            Bounds = new Rectangle(30, 93, 390, 22),
            BackColor = canvas,
            ForeColor = textColor,
            Font = new Font("Bahnschrift", 9F)
        });
        _currentStepLabel = new Label
        {
            Bounds = new Rectangle(520, 68, 168, 34),
            BackColor = Acid,
            ForeColor = Ink,
            Font = new Font("Consolas", 9F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_currentStepLabel);

        var titles = new[]
        {
            "START THE ACTIVITY",
            "ENABLE NO-SAVE",
            "COMPLETE THE ACTIVITY",
            "RETURN TO STORY MODE",
            "DISABLE NO-SAVE",
            "RETURN TO GTA ONLINE"
        };
        var descriptions = new[]
        {
            "Launch a high-value mission or heist: Cayo Perico, Kortz Center, Diamond Casino, Doomsday, Dr. Dre, or another activity.",
            "Activate no-save as soon as the activity starts. Keep it active until the activity has been completed.",
            "Avoid failing if you want the Elite Challenge and its bonus. A failure normally removes only the associated bonus.",
            "After a successful completion, leave GTA Online and wait until Story Mode has fully loaded.",
            "Disable no-save and verify that the application clearly displays INACTIVE before continuing.",
            "Rejoin GTA Online. The reward should remain available while the activity can generally be replayed."
        };

        for (var index = 0; index < titles.Length; index++)
        {
            var stepNumber = index + 1;
            var panel = BuildStep(stepNumber, titles[index], descriptions[index],
                new Rectangle(28, 120 + index * 64, 664, 56), darkMode);
            foreach (Control child in panel.Controls)
            {
                child.Click += (_, _) =>
                {
                    panel.Focus();
                    SetCurrentStep(stepNumber, persist: true);
                };
            }
            panel.Click += (_, _) =>
            {
                panel.Focus();
                SetCurrentStep(stepNumber, persist: true);
            };
            var stepIndex = index;
            panel.KeyDown += (_, eventArgs) =>
            {
                if (eventArgs.KeyCode is Keys.Enter or Keys.Space)
                {
                    SetCurrentStep(stepNumber, persist: true);
                    eventArgs.Handled = true;
                }
                else if (eventArgs.KeyCode is Keys.Down or Keys.Right && stepIndex < 5)
                {
                    _steps[stepIndex + 1].Focus();
                    SetCurrentStep(stepIndex + 2, persist: true);
                    eventArgs.Handled = true;
                }
                else if (eventArgs.KeyCode is Keys.Up or Keys.Left && stepIndex > 0)
                {
                    _steps[stepIndex - 1].Focus();
                    SetCurrentStep(stepIndex, persist: true);
                    eventArgs.Handled = true;
                }
            };
            panel.Enter += (_, _) => panel.Invalidate();
            panel.Leave += (_, _) => panel.Invalidate();
            panel.Paint += (_, eventArgs) =>
            {
                if (panel.Focused)
                {
                    ControlPaint.DrawFocusRectangle(eventArgs.Graphics,
                        Rectangle.Inflate(panel.ClientRectangle, -4, -4));
                }
            };
            _steps[index] = panel;
            Controls.Add(panel);
        }

        Controls.Add(new Label
        {
            Text = "TIP — COOLDOWN\n" +
                   "Group heists: 48 minutes. Solo Cayo Perico: 144 minutes. " +
                   "Other activities may use a different timer.",
            Bounds = new Rectangle(28, 510, 664, 48),
            BackColor = Blue,
            ForeColor = Ink,
            Font = new Font("Bahnschrift", 8.5F, FontStyle.Bold),
            Padding = new Padding(14, 6, 14, 6)
        });
        Controls.Add(new Label
        {
            Text = "WARNING — USE AT YOUR OWN RISK\n" +
                   "Online exploits may cause progress loss, transaction rollback, suspension, or account sanctions. " +
                   "The perceived risk may be low, but no method is completely risk-free.",
            Bounds = new Rectangle(28, 568, 664, 78),
            BackColor = AlertRed,
            ForeColor = Ink,
            Font = new Font("Bahnschrift", 9F, FontStyle.Bold),
            Padding = new Padding(14, 9, 14, 9)
        });
        var closeButton = CreateButton("GOT IT", new Rectangle(582, 654, 110, 36), Ink, Paper);
        closeButton.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        Controls.Add(closeButton);
        AcceptButton = closeButton;
        SetCurrentStep(GuideProgressSettings.Load(), persist: false);
    }

    protected override void OnShown(EventArgs e)
    {
        var workingArea = Screen.FromControl(Owner ?? this).WorkingArea;
        var dpiScale = DeviceDpi / 96F;
        var maximumWidth = Math.Max(320, (int)(workingArea.Width * 0.9));
        var maximumHeight = Math.Max(320, (int)(workingArea.Height * 0.9));
        ClientSize = new Size(
            Math.Min((int)Math.Round(720 * dpiScale), maximumWidth),
            Math.Min((int)Math.Round(700 * dpiScale), maximumHeight));
        CenterToParent();
        base.OnShown(e);
    }

    private static Size GetGuideSize()
    {
        var workingHeight = Screen.PrimaryScreen?.WorkingArea.Height ?? 768;
        return new Size(720, Math.Min(700, Math.Max(520, (int)(workingHeight * 0.9))));
    }

    private static GuideStepPanel BuildStep(
        int number, string title, string description, Rectangle bounds,
        bool darkMode)
    {
        var neutral = darkMode ? DarkSurface : Color.FromArgb(246, 242, 228);
        var textColor = darkMode ? Paper : Ink;
        var panel = new GuideStepPanel
        {
            Bounds = bounds,
            BackColor = neutral,
            BorderStyle = BorderStyle.FixedSingle,
            Cursor = Cursors.Hand,
            TabStop = true,
            AccessibleRole = AccessibleRole.RadioButton,
            AccessibleName = $"Step {number}: {title}",
            AccessibleDescription = description,
            AccessibleDefaultActionDescription = "Mark as current step"
        };
        panel.Controls.Add(new Label
        {
            Text = number.ToString("00"),
            Bounds = new Rectangle(0, 0, 58, 60),
            BackColor = Yellow,
            ForeColor = Ink,
            Font = new Font("Impact", 18F),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand
        });
        panel.Controls.Add(new Label
        {
            Text = title,
            Bounds = new Rectangle(72, 4, 565, 20),
            BackColor = panel.BackColor,
            ForeColor = textColor,
            Font = new Font("Bahnschrift", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        });
        panel.Controls.Add(new Label
        {
            Text = description,
            Bounds = new Rectangle(72, 23, 576, 30),
            BackColor = panel.BackColor,
            ForeColor = textColor,
            Font = new Font("Bahnschrift", 8.4F),
            Cursor = Cursors.Hand
        });
        return panel;
    }

    private void SetCurrentStep(int step, bool persist)
    {
        _currentStepLabel.Text = $"CURRENT STEP  {step} / 6";
        for (var index = 0; index < _steps.Length; index++)
        {
            var color = index == step - 1
                ? Acid
                : _darkMode ? DarkSurface : Color.FromArgb(246, 242, 228);
            _steps[index].IsCurrent = index == step - 1;
            _steps[index].BackColor = color;
            for (var childIndex = 1; childIndex < _steps[index].Controls.Count; childIndex++)
            {
                _steps[index].Controls[childIndex].BackColor = color;
                _steps[index].Controls[childIndex].ForeColor =
                    index == step - 1 || !_darkMode ? Ink : Paper;
            }
        }
        if (persist)
        {
            try
            {
                GuideProgressSettings.Save(step);
            }
            catch
            {
                // Progress tracking is optional and must never block the guide.
            }
        }
    }
}

internal sealed class GuideStepPanel : Panel
{
    private bool _isCurrent;

    internal bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
            {
                return;
            }
            _isCurrent = value;
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() =>
        new GuideStepAccessibleObject(this);

    internal void PerformDefaultAction() => OnClick(EventArgs.Empty);

    private sealed class GuideStepAccessibleObject(GuideStepPanel owner)
        : ControlAccessibleObject(owner)
    {
        public override AccessibleStates State =>
            base.State |
            (owner.IsCurrent
                ? AccessibleStates.Checked | AccessibleStates.Selected
                : AccessibleStates.None);

        public override void DoDefaultAction() => owner.PerformDefaultAction();
    }
}

internal sealed class StatusToastForm : Form
{
    private static readonly Color Ink = Color.FromArgb(17, 17, 17);
    private static readonly Color Paper = Color.FromArgb(255, 253, 245);
    private readonly System.Windows.Forms.Timer _closeTimer;

    internal StatusToastForm(string title, string detail, Color accent)
    {
        var hasDetail = !string.IsNullOrWhiteSpace(detail);
        ClientSize = new Size(360, hasDetail ? 116 : 88);
        BackColor = Ink;
        ForeColor = Paper;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AccessibleName = title;
        AccessibleRole = AccessibleRole.Alert;

        Controls.Add(new Label
        {
            Text = title,
            Bounds = new Rectangle(18, 12, 324, 30),
            BackColor = Ink,
            ForeColor = accent,
            Font = new Font("Impact", 18F),
            TextAlign = ContentAlignment.MiddleLeft
        });
        Controls.Add(new Label
        {
            Text = hasDetail ? detail : "VAULTLOOP // GTA ONLY",
            Bounds = new Rectangle(20, 45, 320, hasDetail ? 54 : 26),
            BackColor = Ink,
            ForeColor = Paper,
            Font = new Font("Bahnschrift", 8.5F, FontStyle.Bold),
            AutoEllipsis = true
        });

        _closeTimer = new System.Windows.Forms.Timer { Interval = hasDetail ? 8000 : 2200 };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            Close();
        };
        Shown += (_, _) =>
        {
            var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(
                workingArea.Right - Width - 24,
                workingArea.Bottom - Height - 24);
            AccessibilityNotifyClients(AccessibleEvents.SystemAlert, -1);
            _closeTimer.Start();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int ToolWindow = 0x00000080;
            const int NoActivate = 0x08000000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= ToolWindow | NoActivate;
            return parameters;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Paper, 3F);
        e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closeTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class BooleanToggle : Control
{
    private static readonly Color Ink = Color.FromArgb(17, 17, 17);
    private static readonly Color Paper = Color.FromArgb(255, 253, 245);
    private static readonly Color Yellow = Color.FromArgb(255, 215, 56);
    private static readonly Color Acid = Color.FromArgb(185, 255, 61);
    private static readonly Color HotPink = Color.FromArgb(255, 83, 112);
    private bool _checked;
    private bool _isStateKnown = true;
    private bool _isRecoveryMode;

    internal bool IsRecoveryMode
    {
        get => _isRecoveryMode;
        set
        {
            if (_isRecoveryMode == value)
            {
                return;
            }
            _isRecoveryMode = value;
            AccessibleDefaultActionDescription = value
                ? "Restore firewall rule"
                : Checked ? "Disable no-save" : "Enable no-save";
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            Invalidate();
        }
    }

    internal bool IsStateKnown
    {
        get => _isStateKnown;
        set
        {
            if (_isStateKnown == value)
            {
                return;
            }
            _isStateKnown = value;
            AccessibleDefaultActionDescription = value
                ? (Checked ? "Disable no-save" : "Enable no-save")
                : "State unavailable";
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            Invalidate();
        }
    }

    internal bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
            {
                return;
            }
            _checked = value;
            AccessibleDefaultActionDescription = value ? "Disable no-save" : "Enable no-save";
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            Invalidate();
        }
    }

    internal event EventHandler? ToggleRequested;

    internal BooleanToggle()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                 ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.CheckButton;
        AccessibleDefaultActionDescription = "Enable no-save";
    }

    protected override AccessibleObject CreateAccessibilityInstance() =>
        new BooleanToggleAccessibleObject(this);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var track = new Rectangle(2, 2, Width - 12, Height - 12);
        var shadow = new Rectangle(track.X + 8, track.Y + 8, track.Width, track.Height);
        var knobWidth = 94;
        var knob = new Rectangle(
            Checked ? track.Right - knobWidth - 8 : track.X + 8,
            track.Y + 8, knobWidth, track.Height - 16);
        var labelArea = Checked
            ? new Rectangle(track.X + 8, track.Y, track.Width - knobWidth - 22, track.Height)
            : new Rectangle(track.X + knobWidth + 14, track.Y, track.Width - knobWidth - 22, track.Height);

        using var shadowBrush = new SolidBrush(Ink);
        using var trackBrush = new SolidBrush(
            !IsStateKnown ? Yellow : Checked ? HotPink : Acid);
        using var knobBrush = new SolidBrush(Paper);
        using var borderPen = new Pen(Ink, 4F);
        using var labelFont = new Font("Impact", 23F);
        using var knobFont = new Font("Consolas", 9F, FontStyle.Bold);

        e.Graphics.FillRectangle(shadowBrush, shadow);
        e.Graphics.FillRectangle(trackBrush, track);
        e.Graphics.DrawRectangle(borderPen, track);
        if (IsRecoveryMode)
        {
            TextRenderer.DrawText(e.Graphics, "RESTORE", labelFont, track, Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else if (!IsStateKnown)
        {
            TextRenderer.DrawText(e.Graphics, "STATE ?", labelFont, track, Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else
        {
            e.Graphics.FillRectangle(knobBrush, knob);
            e.Graphics.DrawRectangle(borderPen, knob);
            TextRenderer.DrawText(e.Graphics, Checked ? "ON" : "OFF", labelFont, labelArea, Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics, "NO-SAVE", knobFont, knob, Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        if (Focused)
        {
            var focus = Rectangle.Inflate(track, -7, -7);
            ControlPaint.DrawFocusRectangle(e.Graphics, focus, Ink, Checked ? HotPink : Acid);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        RequestToggle();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            RequestToggle();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void RequestToggle()
    {
        if (Enabled && (IsStateKnown || IsRecoveryMode))
        {
            ToggleRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class BooleanToggleAccessibleObject(BooleanToggle owner)
        : ControlAccessibleObject(owner)
    {
        public override string? DefaultAction =>
            owner.IsRecoveryMode ? "Restore firewall rule" :
            !owner.IsStateKnown ? "State unavailable" :
            owner.Checked ? "Disable no-save" : "Enable no-save";

        public override AccessibleStates State =>
            base.State |
            (owner.IsStateKnown && owner.Checked ? AccessibleStates.Checked : AccessibleStates.None);

        public override void DoDefaultAction() => owner.RequestToggle();
    }
}
