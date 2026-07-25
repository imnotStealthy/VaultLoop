using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

internal sealed class ThemeController
{
    private readonly Form _form;
    private readonly Button _themeButton;
    private readonly Control[] _excludedControls;
    private readonly Dictionary<Control, Color> _originalBackColors = new();
    private readonly Dictionary<Control, Color> _originalForeColors = new();

    internal ThemeController(
        Form form, Button themeButton, Control[] excludedControls)
    {
        _form = form;
        _themeButton = themeButton;
        _excludedControls = excludedControls;
    }

    internal void CaptureThemeColors()
    {
        CaptureThemeColors(_form);
    }

    internal void ApplyTheme(bool darkMode)
    {
        _form.BackColor = darkMode ? Palette.DarkCanvas : Palette.Cream;
        _form.ForeColor = darkMode ? Palette.Paper : Palette.Ink;
        foreach (var entry in _originalBackColors)
        {
            var originalBack = entry.Value;
            var mappedBack = originalBack == Palette.Cream
                ? (darkMode ? Palette.DarkCanvas : Palette.Cream)
                : originalBack == Palette.Paper
                    ? (darkMode ? Palette.DarkSurface : Palette.Paper)
                    : originalBack;
            entry.Key.BackColor = mappedBack;

            var originalFore = _originalForeColors[entry.Key];
            entry.Key.ForeColor = darkMode && originalFore == Palette.Ink &&
                                  (originalBack == Palette.Cream ||
                                   originalBack == Palette.Paper)
                ? Palette.Paper
                : originalFore;
        }
        _themeButton.Text = darkMode ? "LIGHT THEME" : "DARK THEME";
        _themeButton.AccessibleName = darkMode
            ? "Switch to light theme"
            : "Switch to dark theme";
        _form.Invalidate(true);
    }

    private void CaptureThemeColors(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (!IsExcluded(control))
            {
                _originalBackColors[control] = control.BackColor;
                _originalForeColors[control] = control.ForeColor;
            }
            CaptureThemeColors(control);
        }
    }

    private bool IsExcluded(Control control)
    {
        foreach (var excludedControl in _excludedControls)
        {
            if (ReferenceEquals(control, excludedControl))
            {
                return true;
            }
        }
        return false;
    }
}
