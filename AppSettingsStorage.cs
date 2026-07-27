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
            EnsureNotReparsePoint(currentPath);
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

        EnsureNotReparsePoint(legacyPath);
        fromLegacy = true;
        return File.ReadAllText(legacyPath, Encoding.UTF8);
    }

    /// <summary>
    /// Reads a preference, parses it with <paramref name="parse"/>, and rewrites a value that
    /// came from the legacy directory into the current one. A preference is never worth
    /// failing a start over: an unreadable file, an unparsable value, or a migration that
    /// cannot be persisted all fall back to <paramref name="fallback"/> or to the value in
    /// hand.
    /// </summary>
    /// <param name="parse">
    /// Returns <c>false</c> when the stored text does not describe a usable value.
    /// </param>
    internal static T ReadPreference<T>(
        string fileName, bool includeLegacy, TryParse<T> parse, Action<T> save, T fallback)
    {
        try
        {
            var rawValue = ReadText(fileName, includeLegacy, out var fromLegacy);
            if (rawValue is null || !parse(rawValue, out var value))
            {
                return fallback;
            }

            if (fromLegacy)
            {
                try
                {
                    save(value);
                }
                catch
                {
                    // A usable legacy preference stays usable even if it cannot be migrated.
                }
            }
            return value;
        }
        catch
        {
            return fallback;
        }
    }

    internal delegate bool TryParse<T>(string rawValue, out T value);

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
        EnsureNotReparsePoint(destination);
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

    /// <summary>
    /// Rejects a settings file that is a symbolic link, junction, or other reparse point.
    /// VaultLoop runs elevated but its settings live under %LOCALAPPDATA%, which the
    /// unprivileged user controls; following a redirection from there would let that user
    /// steer an administrator-level read or write somewhere else. The directory is already
    /// checked in <see cref="WriteText"/> — this covers the files themselves, on both paths.
    /// </summary>
    private static void EnsureNotReparsePoint(string path)
    {
        if (File.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The VaultLoop settings file cannot be a reparse point: {path}");
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
