using System.Drawing;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

internal sealed class ShortcutDialog : BrutalistDialog
{
    private const string CaptureHint =
        "Press a new combination. Use a modifier, or a function key.";
    private const string RejectedHint =
        "Not accepted. Press a function key, or hold Ctrl, Alt, or Shift with a key.";

    private readonly Button _capturedButton;
    private readonly Label _keyboardHint;
    private readonly Color _hintColor;
    private readonly Button _controllerCaptureButton;
    private readonly Button _configureControllerButton;
    private readonly Label _controllerHint;
    private readonly ControllerShortcutService? _controllerService;
    private readonly System.Windows.Forms.Timer _controllerTimer;
    private bool _controllerCaptureActive;

    internal Keys ShortcutModifiers { get; private set; }
    internal Keys ShortcutKey { get; private set; }
    internal ControllerShortcut? ControllerShortcut { get; private set; }

    internal ShortcutDialog(
        Keys modifiers, Keys key, bool darkMode,
        ControllerShortcutService? controllerService = null,
        ControllerShortcut? controllerShortcut = null) :
        base("CONFIGURE SHORTCUT", new Size(520, 500),
            darkMode ? Palette.DarkCanvas : Palette.Yellow)
    {
        ShortcutModifiers = modifiers;
        ShortcutKey = key;
        ControllerShortcut = controllerShortcut;
        _controllerService = controllerService;
        // The capture used to be bound to the capture field alone, so every key was ignored
        // once the focus had moved — clicking REPLACE or CLEAR for the controller was enough,
        // and the dialog then looked like it had stopped reading the keyboard. The whole
        // dialog listens instead; navigation keys are still left to the dialog.
        KeyPreview = true;
        KeyDown += CaptureShortcut;
        var canvas = darkMode ? Palette.DarkCanvas : Palette.Yellow;
        var textColor = darkMode ? Palette.Paper : Palette.Ink;

        Controls.Add(new Label
        {
            Text = "KEYBOARD SHORTCUT",
            Bounds = new Rectangle(28, 58, 460, 34),
            Font = Typography.DialogHeading,
            BackColor = canvas,
            ForeColor = textColor
        });
        _hintColor = textColor;
        _keyboardHint = new Label
        {
            Text = CaptureHint,
            Bounds = new Rectangle(30, 96, 460, 28),
            BackColor = canvas,
            ForeColor = textColor
        };
        _keyboardHint.Name = "KeyboardShortcutHint";
        Controls.Add(_keyboardHint);

        _capturedButton = BrutalistControls.CreateOutlinedButton(
            ShortcutSettings.Format(modifiers, key), new Rectangle(30, 128, 460, 48),
            Typography.StatusDetail, darkMode ? Palette.DarkSurface : Palette.Paper,
            darkMode ? Palette.Paper : Palette.Ink);
        _capturedButton.Name = "ShortcutCapture";
        _capturedButton.AccessibleName = "Keyboard shortcut capture field";
        _capturedButton.AccessibleDescription =
            "Press the shortcut you want to use anywhere in this dialog.";
        _capturedButton.Font = Typography.ShortcutCapture;
        Controls.Add(_capturedButton);

        Controls.Add(new Label
        {
            Text = "CONTROLLER SHORTCUT",
            Bounds = new Rectangle(28, 194, 460, 34),
            Font = Typography.DialogHeading,
            BackColor = canvas,
            ForeColor = textColor
        });
        Controls.Add(new Label
        {
            Text = "Hold an exact combination of 2 or 3 buttons, then release all buttons.",
            Bounds = new Rectangle(30, 232, 460, 32),
            BackColor = canvas,
            ForeColor = textColor
        });

        _controllerCaptureButton = BrutalistControls.CreateOutlinedButton(
            ControllerShortcut?.Format() ?? "NOT CONFIGURED",
            new Rectangle(30, 270, 460, 56), Typography.StatusDetail,
            darkMode ? Palette.DarkSurface : Palette.Paper,
            darkMode ? Palette.Paper : Palette.Ink);
        _controllerCaptureButton.Name = "ControllerShortcutCapture";
        _controllerCaptureButton.AccessibleName = "Controller shortcut capture status";
        _controllerCaptureButton.TabStop = false;

        _configureControllerButton = BrutalistControls.CreateOutlinedButton(
            ControllerShortcut is null ? "CONFIGURE" : "REPLACE",
            new Rectangle(30, 340, 180, 38), Typography.StatusDetail,
            Palette.Acid, Palette.Ink);
        _configureControllerButton.Name = "ConfigureControllerShortcut";
        _configureControllerButton.AccessibleName = "Configure controller shortcut";
        _configureControllerButton.Enabled = controllerService is not null;
        _configureControllerButton.Click += (_, _) => ToggleControllerCapture();

        var clearControllerButton = BrutalistControls.CreateOutlinedButton(
            "CLEAR", new Rectangle(220, 340, 100, 38), Typography.StatusDetail,
            darkMode ? Palette.DarkSurface : Palette.Paper,
            darkMode ? Palette.Paper : Palette.Ink);
        clearControllerButton.AccessibleName = "Clear controller shortcut";
        clearControllerButton.Enabled = ControllerShortcut is not null;

        _controllerHint = new Label
        {
            Text = controllerService is null
                ? "Controller input is unavailable."
                : ControllerShortcut is null
                    ? "Controller shortcut disabled until configured."
                    : "This controller toggles no-save ON or OFF.",
            Bounds = new Rectangle(30, 390, 460, 28),
            BackColor = canvas,
            ForeColor = textColor
        };
        clearControllerButton.Click += (_, _) =>
        {
            StopControllerCapture();
            ControllerShortcut = null;
            _controllerCaptureButton.Text = "NOT CONFIGURED";
            _configureControllerButton.Text = "CONFIGURE";
            clearControllerButton.Enabled = false;
            _controllerHint.Text = "Controller shortcut disabled until configured.";
        };
        Controls.AddRange([
            _controllerCaptureButton, _configureControllerButton,
            clearControllerButton, _controllerHint]);

        _controllerTimer = new System.Windows.Forms.Timer
        {
            Interval = 50
        };
        _controllerTimer.Tick += (_, _) =>
        {
            if (_controllerService is null)
            {
                return;
            }

            var snapshot = _controllerService.CaptureSnapshot;
            _controllerCaptureButton.Text = snapshot.StatusText;
            if (snapshot.Retry)
            {
                _controllerCaptureActive = false;
                _controllerTimer.Stop();
                _configureControllerButton.Text = "RETRY";
                _controllerHint.Text =
                    "The configured controller disconnected during capture.";
                return;
            }
            if (!snapshot.Complete || snapshot.Shortcut is null)
            {
                return;
            }

            ControllerShortcut = snapshot.Shortcut;
            _controllerCaptureActive = false;
            _controllerTimer.Stop();
            _configureControllerButton.Text = "REPLACE";
            clearControllerButton.Enabled = true;
            _controllerHint.Text =
                "In GTA, hold 0.5 s to toggle ON or OFF. Click SAVE to apply.";
        };

        var secondaryColor = darkMode ? Palette.DarkSurface : Palette.Paper;
        var secondaryText = darkMode ? Palette.Paper : Palette.Ink;
        var resetButton = BrutalistControls.CreateOutlinedButton(
            "RESET", new Rectangle(30, 438, 86, 36), Typography.StatusDetail,
            secondaryColor, secondaryText);
        var saveButton = BrutalistControls.CreateOutlinedButton(
            "SAVE", new Rectangle(310, 438, 84, 36), Typography.StatusDetail,
            Palette.Acid, Palette.Ink);
        var cancelButton = BrutalistControls.CreateOutlinedButton(
            "CANCEL", new Rectangle(404, 438, 86, 36), Typography.StatusDetail,
            secondaryColor, secondaryText);
        resetButton.Click += (_, _) =>
        {
            (ShortcutModifiers, ShortcutKey) = ShortcutSettings.Default;
            _capturedButton.Text = ShortcutSettings.Format(ShortcutModifiers, ShortcutKey);
        };
        saveButton.Click += (_, _) =>
        {
            StopControllerCapture();
            DialogResult = DialogResult.OK;
            Close();
        };
        cancelButton.Click += (_, _) =>
        {
            StopControllerCapture();
            DialogResult = DialogResult.Cancel;
            Close();
        };
        Controls.AddRange([resetButton, saveButton, cancelButton]);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Shown += (_, _) => _capturedButton.Focus();
        FormClosed += (_, _) =>
        {
            StopControllerCapture();
            _controllerTimer.Dispose();
        };
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
        var outcome = TryCapture(eventArgs.KeyCode, eventArgs.Modifiers);
        if (outcome == CaptureOutcome.Rejected)
        {
            System.Media.SystemSounds.Beep.Play();
        }
        if (outcome != CaptureOutcome.Accepted)
        {
            return;
        }
        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
    }

    /// <summary>
    /// Applies one key press to the captured shortcut. A press that does not describe a usable
    /// shortcut says so on screen: the rule — a function key, or a modifier with a key — used
    /// to be enforced by a system beep alone, which is inaudible on a muted machine and left
    /// the dialog looking unresponsive.
    /// </summary>
    internal CaptureOutcome TryCapture(Keys key, Keys modifiers)
    {
        if (key is Keys.Tab or Keys.Escape or Keys.Enter or Keys.Space or
            Keys.Left or Keys.Right or Keys.Up or Keys.Down or
            Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown)
        {
            return CaptureOutcome.Ignored;
        }
        if (key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey)
        {
            return CaptureOutcome.Ignored;
        }

        var capturedModifiers = modifiers & (Keys.Control | Keys.Alt | Keys.Shift);
        if (!IsValidShortcut(capturedModifiers, key))
        {
            _keyboardHint.Text = RejectedHint;
            _keyboardHint.ForeColor = Palette.HotPink;
            return CaptureOutcome.Rejected;
        }

        ShortcutModifiers = capturedModifiers;
        ShortcutKey = key;
        _capturedButton.Text = ShortcutSettings.Format(capturedModifiers, key);
        _keyboardHint.Text = CaptureHint;
        _keyboardHint.ForeColor = _hintColor;
        return CaptureOutcome.Accepted;
    }

    /// <summary>What one key press did to the captured shortcut.</summary>
    internal enum CaptureOutcome
    {
        /// <summary>A navigation or modifier key the dialog leaves alone.</summary>
        Ignored,

        /// <summary>A key press that does not describe a usable shortcut.</summary>
        Rejected,

        /// <summary>The captured shortcut now describes this press.</summary>
        Accepted
    }

    private void ToggleControllerCapture()
    {
        if (_controllerService is null)
        {
            return;
        }
        if (_controllerCaptureActive)
        {
            StopControllerCapture();
            _controllerCaptureButton.Text =
                ControllerShortcut?.Format() ?? "NOT CONFIGURED";
            return;
        }

        _controllerCaptureActive = true;
        _configureControllerButton.Text = "CANCEL CAPTURE";
        _controllerHint.Text = _controllerService.RawInputAvailable
            ? "Xbox, DualShock 4 and DualSense input ready."
            : "Xbox ready. PlayStation input is unavailable.";
        _controllerService.BeginCapture();
        _controllerTimer.Start();
    }

    private void StopControllerCapture()
    {
        if (!_controllerCaptureActive)
        {
            return;
        }
        _controllerCaptureActive = false;
        _controllerTimer.Stop();
        _controllerService?.CancelCapture();
        _configureControllerButton.Text =
            ControllerShortcut is null ? "CONFIGURE" : "REPLACE";
    }
}
