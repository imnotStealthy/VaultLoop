using System;

namespace ReplayGlitchGTA;

internal static class HudSettings
{
    /// <summary>
    /// The HUD is on until the user turns it off, so an unreadable or missing file means on.
    /// The file is new in this version and has no legacy counterpart to migrate.
    /// </summary>
    internal static bool Load() =>
        AppSettingsStorage.ReadPreference<bool>(
            "hud.txt", includeLegacy: false, TryParse, Save, fallback: true);

    /// <summary>Anything other than "off" means the HUD is on, so parsing never fails.</summary>
    private static bool TryParse(string rawValue, out bool hudEnabled)
    {
        hudEnabled = !rawValue.Trim().Equals("off", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    internal static void Save(bool hudEnabled)
    {
        AppSettingsStorage.WriteText("hud.txt", hudEnabled ? "on" : "off");
    }
}
