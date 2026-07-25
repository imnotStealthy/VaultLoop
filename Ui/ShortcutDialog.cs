using System.Drawing;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

internal sealed class ShortcutDialog : BrutalistDialog
{
    private readonly Button _capturedButton;

    internal Keys ShortcutModifiers { get; private set; }
    internal Keys ShortcutKey { get; private set; }

    internal ShortcutDialog(Keys modifiers, Keys key, bool darkMode) :
        base("CONFIGURE SHORTCUT", new Size(430, 280),
            darkMode ? Palette.DarkCanvas : Palette.Yellow)
    {
        ShortcutModifiers = modifiers;
        ShortcutKey = key;
        KeyPreview = false;
        var canvas = darkMode ? Palette.DarkCanvas : Palette.Yellow;
        var textColor = darkMode ? Palette.Paper : Palette.Ink;

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
            darkMode ? Palette.DarkSurface : Palette.Paper,
            darkMode ? Palette.Paper : Palette.Ink);
        _capturedButton.Name = "ShortcutCapture";
        _capturedButton.AccessibleName = "Keyboard shortcut capture field";
        _capturedButton.AccessibleDescription =
            "Focus this control and press the shortcut you want to use.";
        _capturedButton.Font = new Font("Consolas", 16F, FontStyle.Bold);
        _capturedButton.KeyDown += CaptureShortcut;
        Controls.Add(_capturedButton);

        var secondaryColor = darkMode ? Palette.DarkSurface : Palette.Paper;
        var secondaryText = darkMode ? Palette.Paper : Palette.Ink;
        var resetButton = CreateButton("RESET", new Rectangle(30, 218, 86, 36),
            secondaryColor, secondaryText);
        var saveButton = CreateButton("SAVE", new Rectangle(220, 218, 84, 36), Palette.Acid, Palette.Ink);
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
