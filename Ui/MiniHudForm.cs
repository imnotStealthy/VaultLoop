using System.Drawing;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

internal sealed class MiniHudForm : Form
{
    private readonly Label _statusLabel;

    internal MiniHudForm()
    {
        ClientSize = new Size(260, 52);
        BackColor = Color.Black;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AccessibleName = "VaultLoop no-save status";

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            ForeColor = Color.White,
            Font = Typography.DialogTitleBar,
            Text = "No-save Disabled",
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_statusLabel);
    }

    internal void ShowOnActiveScreen()
    {
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(workingArea.Right - Width - 24, workingArea.Top + 24);
        Show();
    }

    internal void SetState(bool? enabled)
    {
        _statusLabel.Text = enabled switch
        {
            true => "No-save Enabled",
            false => "No-save Disabled",
            null => "No-save Unknown"
        };
        AccessibleName = _statusLabel.Text;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int ToolWindow = 0x00000080;
            const int Transparent = 0x00000020;
            const int NoActivate = 0x08000000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= ToolWindow | Transparent | NoActivate;
            return parameters;
        }
    }
}
