using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ReplayGlitchGTA;

/// <summary>
/// A short local record of what VaultLoop did to the firewall, and why it stopped.
/// </summary>
/// <remarks>
/// Until now a failure left nothing behind: the user saw a message box, closed it, and there
/// was nothing left to look at afterwards — which made a reported problem impossible to
/// reconstruct. This records only the decisions that matter, keeps the last few hundred of
/// them, and never gets in the way: any failure to write disables it for the rest of the
/// process rather than propagating.
///
/// The whole file is rewritten through <see cref="AppSettingsStorage.WriteText"/> instead of
/// being appended to. That is deliberate. VaultLoop runs elevated while
/// <c>%LOCALAPPDATA%</c> belongs to the unprivileged user, and an append writes into whatever
/// the existing directory entry points at — a hard link to a file elsewhere included, which no
/// reparse-point check catches. The atomic replace already used for preferences writes a fresh
/// file and swaps the name, so a planted link is replaced rather than written through.
/// </remarks>
internal static class ActivityLog
{
    internal const string FileName = "activity.log";

    /// <summary>Enough to cover several sessions; the file stays well under 64 KB.</summary>
    private const int MaximumEntries = 400;

    private const int MaximumMessageLength = 400;

    private static readonly object Sync = new();
    private static readonly Queue<string> Entries = new();
    private static readonly int CurrentProcessId = ReadCurrentProcessId();
    private static bool _loaded;
    private static bool _disabled;

    private static int ReadCurrentProcessId()
    {
        using var process = Process.GetCurrentProcess();
        return process.Id;
    }

    internal static void Write(string message)
    {
        lock (Sync)
        {
            if (_disabled)
            {
                return;
            }

            try
            {
                LoadOnce();
                Entries.Enqueue(Format(message));
                while (Entries.Count > MaximumEntries)
                {
                    Entries.Dequeue();
                }

                var contents = new StringBuilder();
                foreach (var entry in Entries)
                {
                    contents.Append(entry).Append('\n');
                }
                AppSettingsStorage.WriteText(FileName, contents.ToString());
            }
            catch
            {
                // A diagnostic record is never worth failing an operation over, and a storage
                // problem will not fix itself mid-session.
                _disabled = true;
            }
        }
    }

    internal static void Write(string message, Exception exception) =>
        Write($"{message}: {exception.GetType().Name}: {exception.Message}");

    /// <summary>
    /// Seeds the buffer from the existing file once, so the record survives a restart even
    /// though every write rewrites the whole file.
    /// </summary>
    private static void LoadOnce()
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;

        string? existing;
        try
        {
            existing = AppSettingsStorage.ReadText(FileName, includeLegacy: false, out _);
        }
        catch
        {
            return;
        }
        if (existing is null)
        {
            return;
        }

        foreach (var line in existing.Split('\n'))
        {
            var entry = line.TrimEnd('\r');
            if (entry.Length > 0)
            {
                Entries.Enqueue(entry);
            }
        }
        while (Entries.Count > MaximumEntries)
        {
            Entries.Dequeue();
        }
    }

    /// <summary>
    /// One entry is one line: a UTC timestamp, the process that wrote it — the window, the
    /// elevated instance, and the watchdog are three different processes — then the message
    /// with its line breaks removed.
    /// </summary>
    private static string Format(string message)
    {
        var flattened = Flatten(message ?? "");
        if (flattened.Length > MaximumMessageLength)
        {
            flattened = flattened.Substring(0, MaximumMessageLength) + "...";
        }

        var timestamp = DateTime.UtcNow.ToString(
            "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return $"{timestamp}Z  pid {CurrentProcessId,-6}  {flattened}";
    }

    /// <summary>
    /// Collapses every run of whitespace or control characters into a single space. An
    /// exception message carrying a line break must not be able to forge extra log lines.
    /// </summary>
    private static string Flatten(string message)
    {
        var builder = new StringBuilder(message.Length);
        var lastWasSeparator = false;
        foreach (var character in message)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                lastWasSeparator = true;
                continue;
            }
            if (lastWasSeparator && builder.Length > 0)
            {
                builder.Append(' ');
            }
            lastWasSeparator = false;
            builder.Append(character);
        }
        return builder.ToString();
    }
}
