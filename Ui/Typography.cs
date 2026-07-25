using System.Drawing;

namespace ReplayGlitchGTA;

internal static class Typography
{
    internal static readonly Font ProductTitle = new("Impact", 26F);
    internal static readonly Font StatusTitle = new("Impact", 23F);
    internal static readonly Font GuideTitle = new("Impact", 22F);
    internal static readonly Font DialogHeading = new("Impact", 20F);
    internal static readonly Font AccentTitle = new("Impact", 18F);

    internal static readonly Font SectionTitle =
        new("Bahnschrift", 18F, FontStyle.Bold);
    internal static readonly Font WindowTitle =
        new("Bahnschrift", 11F, FontStyle.Bold);
    internal static readonly Font Body =
        new("Bahnschrift", 10F, FontStyle.Regular);
    internal static readonly Font DialogTitleBar =
        new("Bahnschrift", 10F, FontStyle.Bold);
    internal static readonly Font GuideStepTitle =
        new("Bahnschrift", 9.5F, FontStyle.Bold);
    internal static readonly Font GuideHint =
        new("Bahnschrift", 9F, FontStyle.Regular);
    internal static readonly Font StatusDetail =
        new("Bahnschrift", 9F, FontStyle.Bold);
    internal static readonly Font SmallBold =
        new("Bahnschrift", 8.5F, FontStyle.Bold);
    internal static readonly Font GuideStepBody =
        new("Bahnschrift", 8.4F, FontStyle.Regular);
    internal static readonly Font ActionButton =
        new("Bahnschrift", 8F, FontStyle.Bold);

    internal static readonly Font ShortcutCapture =
        new("Consolas", 16F, FontStyle.Bold);
    internal static readonly Font MonoCaption =
        new("Consolas", 10F, FontStyle.Bold);
    internal static readonly Font CompactMono =
        new("Consolas", 9F, FontStyle.Bold);
    internal static readonly Font TinyMono =
        new("Consolas", 8.5F, FontStyle.Bold);
}
