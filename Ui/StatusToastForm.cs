using System.Drawing;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

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
