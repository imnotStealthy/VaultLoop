using System;
using System.IO;
using System.Text;

namespace ReplayGlitchGTA;

internal static class AppSettingsStorage
{
    private const string CurrentDirectoryName = "VaultLoop";
    private const string LegacyDirectoryName = "ReplayGlitchGTA";

    internal static string? ReadText(string fileName, bool includeLegacy, out bool fromLegacy)
    {
        fromLegacy = false;
        var currentPath = GetPath(CurrentDirectoryName, fileName);
        if (File.Exists(currentPath))
        {
            return File.ReadAllText(currentPath, Encoding.UTF8);
        }

        if (!includeLegacy)
        {
            return null;
        }

        var legacyPath = GetPath(LegacyDirectoryName, fileName);
        if (!File.Exists(legacyPath))
        {
            return null;
        }

        fromLegacy = true;
        return File.ReadAllText(legacyPath, Encoding.UTF8);
    }

    internal static void WriteText(string fileName, string value)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            CurrentDirectoryName);
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The VaultLoop settings directory cannot be a reparse point.");
        }

        var destination = Path.Combine(directory, ValidateFileName(fileName));
        var temporary = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, value, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(destination))
            {
                File.Replace(temporary, destination, null);
            }
            else
            {
                File.Move(temporary, destination);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string GetPath(string directoryName, string fileName) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            directoryName,
            ValidateFileName(fileName));

    private static string ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid settings file name.", nameof(fileName));
        }
        return fileName;
    }
}
