using System;
using System.Drawing;
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
        Font = Typography.Body;
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
            Font = Typography.DialogTitleBar,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var closeButton = BrutalistControls.CreateButton(
            "X", new Rectangle(size.Width - 48, 0, 48, 44), Typography.StatusDetail,
            Palette.Ink, Palette.Paper, 3, Palette.Ink, null, null,
            ContentAlignment.MiddleCenter, null);
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

    private void BeginDrag(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left)
        {
            return;
        }
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(Handle, NativeMethods.NonClientLeftButtonDown,
            NativeMethods.HitCaption, 0);
    }
}
