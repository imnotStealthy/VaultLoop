using System.Drawing;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

internal static class BrutalistControls
{
    internal static Button CreateButton(
        string text,
        Rectangle bounds,
        Font font,
        Color backColor,
        Color foreColor,
        int borderSize,
        Color? borderColor,
        Color? hoverBackColor,
        Color? pressedBackColor,
        ContentAlignment textAlignment,
        Color? hoverForeColor)
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
            TextAlign = textAlignment
        };
        button.FlatAppearance.BorderSize = borderSize;
        if (borderColor.HasValue)
        {
            button.FlatAppearance.BorderColor = borderColor.Value;
        }
        if (hoverBackColor.HasValue)
        {
            button.FlatAppearance.MouseOverBackColor = hoverBackColor.Value;
        }
        if (pressedBackColor.HasValue)
        {
            button.FlatAppearance.MouseDownBackColor = pressedBackColor.Value;
        }
        if (hoverForeColor.HasValue)
        {
            var originalForeColor = foreColor;
            button.MouseEnter += (_, _) => button.ForeColor = hoverForeColor.Value;
            button.MouseLeave += (_, _) => button.ForeColor = originalForeColor;
        }
        return button;
    }

    internal static Label MakeLabel(
        string text,
        Rectangle bounds,
        Font font,
        Color backColor,
        Color? foreColor = null,
        ContentAlignment alignment = ContentAlignment.MiddleLeft) =>
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
}
