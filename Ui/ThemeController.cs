using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

/// <summary>
/// Switches the window between the light and dark palettes.
/// </summary>
/// <remarks>
/// The colors captured once at construction are the light-theme originals, and every later
/// switch is computed from them rather than from what is currently on screen: mapping a mapped
/// color would drift a control further away from its palette on each toggle. Controls the form
/// paints itself — the status card and the toggle — are excluded, because their color carries
/// state rather than theme.
/// </remarks>
internal sealed class ThemeController
{
    private readonly Form _form;
    private readonly Button _themeButton;
    private readonly HashSet<Control> _excludedControls;
    private readonly Dictionary<Control, (Color Back, Color Fore)> _originalColors = new();

    internal ThemeController(
        Form form, Button themeButton, Control[] excludedControls)
    {
        _form = form;
        _themeButton = themeButton;
        _excludedControls = new HashSet<Control>(excludedControls);
    }

    internal void CaptureThemeColors()
    {
        CaptureThemeColors(_form);
    }

    internal void ApplyTheme(bool darkMode)
    {
        _form.BackColor = darkMode ? Palette.DarkCanvas : Palette.Cream;
        _form.ForeColor = darkMode ? Palette.Paper : Palette.Ink;
        foreach (var entry in _originalColors)
        {
            var (originalBack, originalFore) = entry.Value;
            entry.Key.BackColor = MapBackColor(originalBack, darkMode);
            entry.Key.ForeColor = MapForeColor(originalFore, originalBack, darkMode);
        }
        _themeButton.Text = darkMode ? "LIGHT THEME" : "DARK THEME";
        _themeButton.AccessibleName = darkMode
            ? "Switch to light theme"
            : "Switch to dark theme";
        _form.Invalidate(true);
    }

    /// <summary>The two neutral surfaces follow the theme; every accent color is left alone.</summary>
    private static Color MapBackColor(Color originalBack, bool darkMode)
    {
        if (originalBack == Palette.Cream)
        {
            return darkMode ? Palette.DarkCanvas : Palette.Cream;
        }
        if (originalBack == Palette.Paper)
        {
            return darkMode ? Palette.DarkSurface : Palette.Paper;
        }
        return originalBack;
    }

    /// <summary>
    /// Dark ink is only lifted to paper where the surface underneath went dark too; text on an
    /// accent color keeps its contrast in both themes.
    /// </summary>
    private static Color MapForeColor(Color originalFore, Color originalBack, bool darkMode) =>
        darkMode && originalFore == Palette.Ink &&
        (originalBack == Palette.Cream || originalBack == Palette.Paper)
            ? Palette.Paper
            : originalFore;

    private void CaptureThemeColors(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (!_excludedControls.Contains(control))
            {
                _originalColors[control] = (control.BackColor, control.ForeColor);
            }
            CaptureThemeColors(control);
        }
    }
}
