using System;
using System.Text;

namespace ReplayGlitchGTA;

internal static class ControllerShortcutSettings
{
    private const string FileName = "controller-shortcut.txt";
    private const string DisabledValue = "disabled";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static ControllerShortcut? Load() =>
        AppSettingsStorage.ReadPreference<ControllerShortcut?>(
            FileName, includeLegacy: false, TryParse, Save, fallback: null);

    internal static void Save(ControllerShortcut? shortcut) =>
        AppSettingsStorage.WriteText(
            FileName, shortcut is null ? DisabledValue : Serialize(shortcut));

    internal static string Serialize(ControllerShortcut shortcut)
    {
        var encodedIdentity = Convert.ToBase64String(
            StrictUtf8.GetBytes(shortcut.DeviceId));
        return $"1|{(int)shortcut.DeviceKind}|{encodedIdentity}|{(uint)shortcut.Buttons}";
    }

    internal static bool TryParse(string rawValue, out ControllerShortcut? shortcut)
    {
        shortcut = null;
        var trimmed = rawValue.Trim();
        if (string.Equals(trimmed, DisabledValue, StringComparison.Ordinal))
        {
            return true;
        }

        var parts = trimmed.Split('|');
        if (parts.Length != 4 || parts[0] != "1" ||
            !int.TryParse(parts[1], out var deviceKindValue) ||
            !uint.TryParse(parts[3], out var buttonsValue))
        {
            return false;
        }

        var deviceKind = (ControllerDeviceKind)deviceKindValue;
        string deviceId;
        try
        {
            deviceId = StrictUtf8.GetString(Convert.FromBase64String(parts[2]));
        }
        catch (FormatException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        var buttons = (ControllerButtons)buttonsValue;
        if (!Enum.IsDefined(typeof(ControllerDeviceKind), deviceKind) ||
            !ControllerShortcut.IsValidDeviceId(deviceKind, deviceId) ||
            !ControllerShortcut.IsValidCombination(buttons))
        {
            return false;
        }

        shortcut = new ControllerShortcut(deviceKind, deviceId, buttons);
        return true;
    }
}
