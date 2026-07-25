using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

internal abstract class BrutalistDialog : Form
{
    protected BrutalistDialog(string title, Size size, Color background)
    {
        Text = title;
        ClientSize = size;
        BackColor = background;
        ForeColor = background == Palette.DarkCanvas ? Palette.Paper : Palette.Ink;
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
            BackColor = Palette.Ink
        };
        var titleLabel = new Label
        {
            Text = title,
            Bounds = new Rectangle(16, 0, size.Width - 68, 44),
            BackColor = Palette.Ink,
            ForeColor = Palette.Paper,
            Font = new Font("Bahnschrift", 10F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var closeButton = CreateButton("X", new Rectangle(size.Width - 48, 0, 48, 44),
            Palette.Ink, Palette.Paper);
        closeButton.AccessibleName = "Close dialog";
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.FlatAppearance.MouseOverBackColor = Palette.AlertRed;
        closeButton.MouseEnter += (_, _) => closeButton.ForeColor = Palette.Ink;
        closeButton.MouseLeave += (_, _) => closeButton.ForeColor = Palette.Paper;
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
        using var pen = new Pen(Palette.Ink, 3F);
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
        button.FlatAppearance.BorderColor = Palette.Ink;
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
