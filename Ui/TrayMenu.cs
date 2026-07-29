using System;
using System.Drawing;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

internal sealed class TrayMenu : ContextMenuStrip
{
    private readonly ToolStripLabel _statusItem;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _hideItem;
    private readonly ToolStripMenuItem _hudItem;
    private readonly ToolStripMenuItem _startupItem;

    internal TrayMenu(
        Action openWindow, Action hideWindow, Action toggleHud,
        Action toggleStartup, Action exit)
    {
        BackColor = Palette.Ink;
        ForeColor = Palette.Paper;
        Font = Typography.TinyMono;
        ShowImageMargin = false;
        ShowCheckMargin = false;
        Padding = new Padding(3, 7, 3, 7);
        MinimumSize = new Size(252, 0);
        Renderer = new TrayMenuRenderer();

        var title = CreateLabel("VAULTLOOP  //  NO-SAVE", "TrayTitle");
        title.Font = Typography.WindowTitle;
        title.ForeColor = Palette.Yellow;

        _statusItem = CreateLabel("STATUS  //  UNKNOWN", "TrayStatus");
        _statusItem.ForeColor = Palette.Yellow;

        _openItem = CreateAction("OPEN VAULTLOOP", "TrayOpen", openWindow);
        _hideItem = CreateAction("HIDE TO TRAY", "TrayHide", hideWindow);
        _hudItem = CreateAction("HUD  //  ON", "TrayHud", toggleHud);
        _startupItem = CreateAction(
            "START WITH WINDOWS  //  OFF", "TrayStartup", toggleStartup);
        var exitItem = CreateAction("EXIT && RESTORE", "TrayExit", exit);
        exitItem.ForeColor = Palette.HotPink;

        Items.AddRange([
            title,
            _statusItem,
            CreateSeparator(),
            _openItem,
            _hideItem,
            _hudItem,
            _startupItem,
            CreateSeparator(),
            exitItem
        ]);
    }

    internal string StatusText => _statusItem.Text;
    internal string HudText => _hudItem.Text;
    internal string StartupText => _startupItem.Text;
    internal bool OpenEnabled => _openItem.Enabled;
    internal bool HideEnabled => _hideItem.Enabled;

    internal void SetStatus(string status, Color color)
    {
        _statusItem.Text = $"STATUS  //  {status}";
        _statusItem.ForeColor = color;
    }

    internal void SetHudEnabled(bool enabled) =>
        _hudItem.Text = enabled ? "HUD  //  ON" : "HUD  //  OFF";

    internal void SetStartupEnabled(bool enabled) =>
        _startupItem.Text =
            enabled ? "START WITH WINDOWS  //  ON" : "START WITH WINDOWS  //  OFF";

    internal void SetWindowVisible(bool visible)
    {
        _openItem.Enabled = !visible;
        _hideItem.Enabled = visible;
    }

    private static ToolStripLabel CreateLabel(string text, string name) =>
        new()
        {
            Name = name,
            Text = text,
            AutoSize = false,
            Size = new Size(240, 31),
            Padding = new Padding(12, 0, 8, 0),
            TextAlign = ContentAlignment.MiddleLeft
        };

    private static ToolStripMenuItem CreateAction(
        string text, string name, Action action)
    {
        var item = new ToolStripMenuItem
        {
            Name = name,
            Text = text,
            AutoSize = false,
            Size = new Size(240, 34),
            Padding = new Padding(12, 0, 8, 0)
        };
        item.Click += (_, _) => action();
        return item;
    }

    private static ToolStripSeparator CreateSeparator() =>
        new()
        {
            AutoSize = false,
            Size = new Size(240, 9),
            Margin = new Padding(0, 2, 0, 2)
        };

    private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        internal TrayMenuRenderer() : base(new TrayColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBorder(
            ToolStripRenderEventArgs eventArgs)
        {
            using var pen = new Pen(Palette.Blue, 2F);
            var bounds = eventArgs.AffectedBounds;
            eventArgs.Graphics.DrawRectangle(
                pen, bounds.X + 1, bounds.Y + 1,
                bounds.Width - 3, bounds.Height - 3);
        }
    }

    private sealed class TrayColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Palette.Ink;
        public override Color ImageMarginGradientBegin => Palette.Ink;
        public override Color ImageMarginGradientMiddle => Palette.Ink;
        public override Color ImageMarginGradientEnd => Palette.Ink;
        public override Color MenuBorder => Palette.Blue;
        public override Color MenuItemBorder => Palette.Acid;
        public override Color MenuItemSelected => Palette.Acid;
        public override Color MenuItemSelectedGradientBegin => Palette.Acid;
        public override Color MenuItemSelectedGradientEnd => Palette.Acid;
        public override Color MenuItemPressedGradientBegin => Palette.Blue;
        public override Color MenuItemPressedGradientEnd => Palette.Blue;
        public override Color SeparatorDark => Palette.Blue;
        public override Color SeparatorLight => Palette.Blue;
    }
}
