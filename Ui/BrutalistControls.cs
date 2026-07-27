using System.Drawing;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

/// <summary>
/// Builds the flat, hard-edged controls the interface is made of.
/// </summary>
internal static class BrutalistControls
{
    /// <summary>
    /// A button drawn as a bordered block: the dialog vocabulary, where the border carries the
    /// shape and the color stays put under the pointer.
    /// </summary>
    internal static Button CreateOutlinedButton(
        string text, Rectangle bounds, Font font, Color backColor, Color foreColor) =>
        CreateButton(text, bounds, new ButtonStyle
        {
            Font = font,
            BackColor = backColor,
            ForeColor = foreColor,
            BorderSize = 3,
            BorderColor = Palette.Ink
        });

    /// <summary>
    /// A borderless button that reacts by swapping its background: the window-chrome
    /// vocabulary, where the surrounding block already provides the edges.
    /// </summary>
    internal static Button CreateChromeButton(
        string text, Rectangle bounds, Font font, Color backColor, Color foreColor,
        Color hoverBackColor, Color? hoverForeColor = null) =>
        CreateButton(text, bounds, new ButtonStyle
        {
            Font = font,
            BackColor = backColor,
            ForeColor = foreColor,
            HoverBackColor = hoverBackColor,
            PressedBackColor = hoverBackColor,
            HoverForeColor = hoverForeColor
        });

    internal static Button CreateButton(string text, Rectangle bounds, ButtonStyle style)
    {
        var button = new Button
        {
            Text = text,
            Bounds = bounds,
            Font = style.Font,
            BackColor = style.BackColor,
            ForeColor = style.ForeColor,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            TextAlign = style.TextAlignment
        };
        button.FlatAppearance.BorderSize = style.BorderSize;
        if (style.BorderColor.HasValue)
        {
            button.FlatAppearance.BorderColor = style.BorderColor.Value;
        }
        if (style.HoverBackColor.HasValue)
        {
            button.FlatAppearance.MouseOverBackColor = style.HoverBackColor.Value;
        }
        if (style.PressedBackColor.HasValue)
        {
            button.FlatAppearance.MouseDownBackColor = style.PressedBackColor.Value;
        }
        if (style.HoverForeColor.HasValue)
        {
            var restingForeColor = style.ForeColor;
            button.MouseEnter += (_, _) => button.ForeColor = style.HoverForeColor.Value;
            button.MouseLeave += (_, _) => button.ForeColor = restingForeColor;
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

    /// <summary>
    /// How one button is painted. Everything except the two colors it always needs has a
    /// default, so a call site only states what makes that button different.
    /// </summary>
    internal sealed class ButtonStyle
    {
        internal Font Font { get; set; } = Typography.Body;
        internal Color BackColor { get; set; } = Palette.Paper;
        internal Color ForeColor { get; set; } = Palette.Ink;
        internal int BorderSize { get; set; }
        internal Color? BorderColor { get; set; }
        internal Color? HoverBackColor { get; set; }
        internal Color? PressedBackColor { get; set; }
        internal Color? HoverForeColor { get; set; }
        internal ContentAlignment TextAlignment { get; set; } = ContentAlignment.MiddleCenter;
    }
}
