using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

/// <summary>
/// The window chrome: fixed geometry, the cards painted behind the controls, and the controls
/// themselves. Kept apart from the runtime behaviour so that changing what the window does and
/// changing what it looks like stay separate edits.
/// </summary>
/// <remarks>
/// Coordinates are absolute against a fixed <see cref="WindowSize"/> client area, because the
/// painted cards and the controls sitting on them have to line up exactly and no layout panel
/// reproduces the offset drop shadows.
/// </remarks>
internal sealed partial class MainForm
{
    private static readonly Size WindowSize = new(780, 520);
    private static readonly Rectangle HeaderCard = new(28, 68, 724, 102);
    private static readonly Rectangle BodyCard = new(28, 194, 724, 228);
    private static readonly Rectangle StatusCard = new(432, 248, 280, 110);
    private static readonly Rectangle FooterCard = new(28, 446, 724, 48);

    /// <summary>
    /// Builds every control and returns the ones the window keeps a handle on. The form's own
    /// constructor owns those fields, so they are handed back rather than assigned here.
    /// </summary>
    private Chrome BuildLayout()
    {
        Text = "VaultLoop";
        ClientSize = WindowSize;
        BackColor = Palette.Cream;
        ForeColor = Palette.Ink;
        Font = Typography.Body;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = Size;
        if (!_previewMode)
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }

        var titleBar = BuildTitleBar();
        Controls.Add(titleBar);
        BuildHeader();
        BuildNoSaveSection(out var toggle, out var kicker, out var title, out var detail);
        BuildFooter(out var shortcutFooter, out var gameStatus);

        return new Chrome
        {
            ShortcutBadge = (Button)titleBar.Controls["ShortcutBadge"]!,
            ThemeButton = (Button)titleBar.Controls["ThemeButton"]!,
            Toggle = toggle,
            StateKicker = kicker,
            StateTitle = title,
            StateDetail = detail,
            ShortcutFooter = shortcutFooter,
            GameStatusLabel = gameStatus
        };
    }

    private Panel BuildTitleBar()
    {
        var titleBar = new Panel
        {
            Bounds = new Rectangle(0, 0, ClientSize.Width, 48),
            BackColor = Palette.Ink
        };

        var logo = new PictureBox
        {
            Bounds = new Rectangle(12, 7, 34, 34),
            Image = _logoImage,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Palette.Yellow,
            TabStop = false
        };
        var title = BrutalistControls.MakeLabel(
            "VAULTLOOP", new Rectangle(58, 7, 280, 34),
            Typography.WindowTitle, Palette.Ink, Palette.Paper);
        var theme = BrutalistControls.CreateChromeButton(
            _darkMode ? "LIGHT THEME" : "DARK THEME",
            new Rectangle(408, 10, 130, 28), Typography.TinyMono,
            Palette.Blue, Palette.Ink, Palette.Blue);
        theme.Name = "ThemeButton";
        theme.AccessibleName = _darkMode ? "Switch to light theme" : "Switch to dark theme";
        theme.Click += (_, _) => ToggleTheme();
        var shortcut = BrutalistControls.CreateChromeButton(
            ShortcutText, new Rectangle(548, 10, 104, 28), Typography.CompactMono,
            Palette.Acid, Palette.Ink, Palette.Acid);
        shortcut.Name = "ShortcutBadge";
        shortcut.AccessibleName = "Configure keyboard shortcut";
        shortcut.Click += (_, _) => ConfigureShortcut();
        var minimize = BrutalistControls.CreateChromeButton(
            "-", new Rectangle(684, 0, 48, 48), Typography.WindowTitle,
            Palette.Ink, Palette.Paper, Palette.Blue, Palette.Ink);
        var close = BrutalistControls.CreateChromeButton(
            "X", new Rectangle(732, 0, 48, 48), Typography.WindowTitle,
            Palette.Ink, Palette.Paper, Palette.HotPink, Palette.Ink);
        minimize.AccessibleName = "Minimize VaultLoop";
        close.AccessibleName = "Close VaultLoop";

        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        close.Click += (_, _) => Close();
        WindowDrag.Attach(this, titleBar, logo, title);

        titleBar.Controls.AddRange([logo, title, theme, shortcut, minimize, close]);
        return titleBar;
    }

    private void BuildHeader()
    {
        Controls.Add(new PictureBox
        {
            Bounds = new Rectangle(49, 81, 76, 76),
            Image = _logoImage,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Palette.Yellow,
            TabStop = false
        });
        Controls.Add(BrutalistControls.MakeLabel(
            "VAULTLOOP / NO-SAVE", new Rectangle(143, 83, 485, 42),
            Typography.ProductTitle, Palette.Yellow));
        Controls.Add(BrutalistControls.MakeLabel("ROCKSTAR CLOUD CONTROL",
            new Rectangle(145, 126, 480, 22), Typography.MonoCaption, Palette.Yellow));

        var guideButton = BrutalistControls.CreateButton(
            "HOW TO USE", new Rectangle(632, 91, 100, 42),
            new BrutalistControls.ButtonStyle
            {
                Font = Typography.ActionButton,
                BackColor = Palette.Ink,
                ForeColor = Palette.Paper,
                BorderSize = 3,
                BorderColor = Palette.Ink,
                HoverBackColor = Palette.Blue,
                HoverForeColor = Palette.Ink
            });
        guideButton.AccessibleName = "Open the no-save instruction guide";
        guideButton.Click += (_, _) =>
        {
            using var guide = new GuideDialog(_darkMode);
            guide.ShowDialog(this);
        };
        Controls.Add(guideButton);
    }

    private void BuildNoSaveSection(
        out BooleanToggle toggle, out Label kicker, out Label title, out Label detail)
    {
        Controls.Add(BrutalistControls.MakeLabel(
            "NO-SAVE MODE", new Rectangle(55, 222, 310, 32),
            Typography.SectionTitle, Palette.Paper));
        Controls.Add(BrutalistControls.MakeLabel(
            "Toggle the Rockstar link without cutting the rest of your network.",
            new Rectangle(56, 258, 320, 44), Typography.Body, Palette.Paper));

        toggle = new BooleanToggle
        {
            Location = new Point(55, 312),
            Size = new Size(315, 86),
            AccessibleName = "Toggle no-save mode",
            AccessibleDescription = "Active blocks the Rockstar link. Inactive restores it."
        };
        toggle.ToggleRequested += (_, _) => ToggleState();
        Controls.Add(toggle);

        kicker = BrutalistControls.MakeLabel(
            "STATUS", new Rectangle(458, 264, 218, 18),
            Typography.CompactMono, Palette.Acid);
        title = BrutalistControls.MakeLabel(
            "", new Rectangle(458, 286, 220, 36),
            Typography.StatusTitle, Palette.Acid);
        detail = BrutalistControls.MakeLabel(
            "", new Rectangle(458, 326, 220, 22),
            Typography.StatusDetail, Palette.Acid);
        Controls.AddRange([kicker, title, detail]);
    }

    private void BuildFooter(out Button shortcutFooter, out Label gameStatus)
    {
        shortcutFooter = BrutalistControls.CreateChromeButton(
            $"{ShortcutText}  //  GTA ONLY", new Rectangle(44, 454, 370, 34),
            Typography.MonoCaption, Palette.Ink, Palette.Paper, Palette.Ink);
        shortcutFooter.AccessibleName = "Configure the GTA-only keyboard shortcut";
        shortcutFooter.Click += (_, _) => ConfigureShortcut();
        Controls.Add(shortcutFooter);

        // Preview renders show the ready state: the elevation prompt only appears on demand.
        var adminReady = _previewMode || Program.IsRunningAsAdministrator();
        gameStatus = BrutalistControls.MakeLabel(
            adminReady ? "WAITING FOR GTA  //  SAFE RESTORE" : "ADMIN ON DEMAND",
            new Rectangle(466, 458, 257, 24), Typography.TinyMono, Palette.Ink,
            adminReady ? Palette.Yellow : Palette.HotPink, ContentAlignment.MiddleCenter);
        Controls.Add(gameStatus);
    }

    private static Image LoadLogo()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("ReplayGlitchLogo.png")
            ?? throw new InvalidOperationException("Embedded logo resource not found.");
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    /// <summary>A filled block with a hard offset shadow and a heavy border.</summary>
    private static void DrawCard(Graphics graphics, Rectangle bounds, Color fill, Color shadow)
    {
        using var shadowBrush = new SolidBrush(shadow);
        using var fillBrush = new SolidBrush(fill);
        using var borderPen = new Pen(Palette.Ink, 4F);
        graphics.FillRectangle(shadowBrush,
            new Rectangle(bounds.X + 8, bounds.Y + 8, bounds.Width, bounds.Height));
        graphics.FillRectangle(fillBrush, bounds);
        graphics.DrawRectangle(borderPen, bounds);
    }

    /// <summary>The controls the window keeps addressing after construction.</summary>
    private sealed class Chrome
    {
        internal Button ShortcutBadge { get; set; } = null!;
        internal Button ThemeButton { get; set; } = null!;
        internal BooleanToggle Toggle { get; set; } = null!;
        internal Label StateKicker { get; set; } = null!;
        internal Label StateTitle { get; set; } = null!;
        internal Label StateDetail { get; set; } = null!;
        internal Button ShortcutFooter { get; set; } = null!;
        internal Label GameStatusLabel { get; set; } = null!;
    }
}
