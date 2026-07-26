using System;

namespace ReplayGlitchGTA;

internal static class ThemeSettings
{
    internal static bool Load()
    {
        try
        {
            var rawValue = AppSettingsStorage.ReadText(
                "theme.txt", includeLegacy: true, out var fromLegacy);
            var darkMode = rawValue?.Trim()
                .Equals("dark", StringComparison.OrdinalIgnoreCase) == true;
            if (fromLegacy)
            {
                try
                {
                    Save(darkMode);
                }
                catch
                {
                }
            }
            return darkMode;
        }
        catch
        {
            return false;
        }
    }

    internal static void Save(bool darkMode)
    {
        AppSettingsStorage.WriteText("theme.txt", darkMode ? "dark" : "light");
    }
}
