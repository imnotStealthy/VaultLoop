using System;
using System.IO;
using Microsoft.Win32;

namespace ReplayGlitchGTA;

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VaultLoop";

    internal static bool IsEnabled(string executablePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var storedCommand = key?.GetValue(
                ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            return string.Equals(
                storedCommand, BuildCommand(executablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static void SetEnabled(string executablePath, bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true) ??
                            throw new InvalidOperationException(
                                "The Windows startup registry key is unavailable.");
            key.SetValue(
                ValueName, BuildCommand(executablePath), RegistryValueKind.String);
            return;
        }

        using var existingKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        existingKey?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    internal static string BuildCommand(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) ||
            executablePath.IndexOfAny(['"', '\r', '\n']) >= 0)
        {
            throw new ArgumentException(
                "The VaultLoop executable path is invalid.", nameof(executablePath));
        }

        return $"\"{Path.GetFullPath(executablePath)}\" --startup";
    }
}
