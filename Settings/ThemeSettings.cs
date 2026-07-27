using System;

namespace ReplayGlitchGTA;

internal static class ThemeSettings
{
    internal static bool Load() =>
        AppSettingsStorage.ReadPreference<bool>(
            "theme.txt", includeLegacy: true, TryParse, Save, fallback: false);

    /// <summary>Anything other than "dark" means the light theme, so parsing never fails.</summary>
    private static bool TryParse(string rawValue, out bool darkMode)
    {
        darkMode = rawValue.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    internal static void Save(bool darkMode)
    {
        AppSettingsStorage.WriteText("theme.txt", darkMode ? "dark" : "light");
    }
}
