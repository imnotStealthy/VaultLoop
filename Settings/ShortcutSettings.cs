using System.Collections.Generic;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

internal static class ShortcutSettings
{
    private const Keys DefaultModifiers = Keys.Control | Keys.Shift;
    private const Keys DefaultKey = Keys.F8;

    internal static (Keys Modifiers, Keys Key) Default => (DefaultModifiers, DefaultKey);

    internal static (Keys Modifiers, Keys Key) Load() =>
        AppSettingsStorage.ReadPreference<(Keys Modifiers, Keys Key)>(
            "shortcut.txt", includeLegacy: true, TryParse,
            shortcut => Save(shortcut.Modifiers, shortcut.Key),
            (DefaultModifiers, DefaultKey));

    internal static void Save(Keys modifiers, Keys key)
    {
        AppSettingsStorage.WriteText("shortcut.txt", $"{(int)modifiers}|{(int)key}");
    }

    /// <summary>
    /// A stored shortcut is only accepted if it still passes the rules the dialog enforces:
    /// the reserved combinations can change between versions, and a saved one must not survive.
    /// </summary>
    private static bool TryParse(string rawValue, out (Keys Modifiers, Keys Key) shortcut)
    {
        shortcut = (DefaultModifiers, DefaultKey);
        var parts = rawValue.Split('|');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var modifiersValue) ||
            !int.TryParse(parts[1], out var keyValue))
        {
            return false;
        }

        var modifiers = (Keys)modifiersValue & Keys.Modifiers;
        var key = (Keys)keyValue & Keys.KeyCode;
        if (!ShortcutDialog.IsValidShortcut(modifiers, key))
        {
            return false;
        }

        shortcut = (modifiers, key);
        return true;
    }

    internal static string Format(Keys modifiers, Keys key)
    {
        var parts = new List<string>();
        if ((modifiers & Keys.Control) != 0) parts.Add("CTRL");
        if ((modifiers & Keys.Alt) != 0) parts.Add("ALT");
        if ((modifiers & Keys.Shift) != 0) parts.Add("SHIFT");
        parts.Add(FormatKey(key));
        return string.Join("+", parts);
    }

    private static string FormatKey(Keys key)
    {
        if (key is >= Keys.D0 and <= Keys.D9)
        {
            return ((int)key - (int)Keys.D0).ToString();
        }
        if (key is >= Keys.NumPad0 and <= Keys.NumPad9)
        {
            return $"NUM {(int)key - (int)Keys.NumPad0}";
        }
        return key.ToString().ToUpperInvariant();
    }
}
