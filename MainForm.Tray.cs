using System;
using System.Drawing;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

internal sealed partial class MainForm
{
    private NotifyIcon? _trayIcon;
    private TrayMenu? _trayMenu;
    private bool _trayHintShown;

    internal void StartInTray()
    {
        if (_previewMode)
        {
            return;
        }

        ShowInTaskbar = false;
        _ = Handle;
        _trayMenu?.SetWindowVisible(visible: false);
        UpdateHudVisibility();
    }

    private void InitializeTray()
    {
        _trayMenu = new TrayMenu(
            ShowFromTray, HideToTray, ToggleHudVisibility,
            ToggleStartWithWindows, ExitFromTray);
        _trayMenu.SetHudEnabled(_hudEnabled);
        _trayMenu.SetStartupEnabled(
            StartupRegistration.IsEnabled(Application.ExecutablePath));
        _trayMenu.SetWindowVisible(visible: false);

        _trayIcon = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Text = "VaultLoop - No-save starting",
            ContextMenuStrip = _trayMenu,
            Visible = true,
            BalloonTipTitle = "VaultLoop",
            BalloonTipText = "VaultLoop is still running in the system tray.",
            BalloonTipIcon = ToolTipIcon.Info
        };
        _trayIcon.MouseDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                ShowFromTray();
            }
        };
    }

    private void ToggleStartWithWindows()
    {
        try
        {
            var enable = !StartupRegistration.IsEnabled(Application.ExecutablePath);
            StartupRegistration.SetEnabled(Application.ExecutablePath, enable);
            _trayMenu?.SetStartupEnabled(enable);
        }
        catch (Exception exception)
        {
            ShowFromTray();
            MessageBox.Show(this,
                $"Start with Windows could not be updated:\n{exception.Message}",
                "Startup setting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void MinimizeToTray()
    {
        if (_previewMode)
        {
            WindowState = FormWindowState.Minimized;
            return;
        }
        HideToTray();
    }

    private void HideToTray()
    {
        if (!Visible)
        {
            return;
        }

        ShowInTaskbar = false;
        Hide();
        _trayMenu?.SetWindowVisible(visible: false);
        UpdateHudVisibility();
        if (!_trayHintShown && _trayIcon is not null)
        {
            _trayHintShown = true;
            _trayIcon.ShowBalloonTip(1600);
        }
    }

    private void ShowFromTray()
    {
        if (IsDisposed)
        {
            return;
        }

        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Show();
        Activate();
        BringToFront();
        _trayMenu?.SetWindowVisible(visible: true);
        UpdateHudVisibility();
    }

    private void ExitFromTray()
    {
        ShowFromTray();
        Close();
    }
}
