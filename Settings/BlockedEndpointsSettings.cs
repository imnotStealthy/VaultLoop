using System;
using System.Collections.Generic;

namespace ReplayGlitchGTA;

/// <summary>
/// Loads the blocked address set from <c>endpoints.txt</c>, so the set can be narrowed or
/// widened between game sessions without a rebuild.
/// </summary>
/// <remarks>
/// Finding the right set is empirical: too narrow and the save goes through, too wide and the
/// rule reaches Rockstar authentication and the session drops instead of only the save
/// failing. Each attempt costs a play session, so the set has to be editable.
///
/// Every entry must sit inside a known Rockstar Online Services allocation. That guard is the
/// reason this file can be user-controlled at all: it keeps a typo, or an over-eager edit,
/// from pointing an elevated firewall rule at Zynga, Take-Two corporate, or the open internet.
/// </remarks>
internal static class BlockedEndpointsSettings
{
    internal const string FileName = "endpoints.txt";

    /// <summary>
    /// Reads the configured set. Returns <c>null</c> when no usable configuration exists, in
    /// which case the caller keeps the built-in default. <paramref name="error"/> describes why
    /// a present file was rejected, so the reason can be surfaced instead of silently ignored.
    /// </summary>
    internal static IReadOnlyList<IpPrefix>? TryLoad(
        IReadOnlyList<IpPrefix> allowedAllocations, out string? error)
    {
        error = null;
        string? rawValue;
        try
        {
            rawValue = AppSettingsStorage.ReadText(FileName, includeLegacy: false, out _);
        }
        catch (Exception exception)
        {
            error = $"{FileName} could not be read ({exception.Message}); using the built-in set.";
            return null;
        }

        if (rawValue is null)
        {
            return null;
        }

        var prefixes = new List<IpPrefix>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;
        foreach (var rawLine in rawValue.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim();
            var comment = line.IndexOf('#');
            if (comment >= 0)
            {
                line = line.Substring(0, comment).Trim();
            }
            if (line.Length == 0)
            {
                continue;
            }

            var prefix = IpPrefix.TryParse(line);
            if (prefix is null)
            {
                error = $"{FileName} line {lineNumber}: '{line}' is not a valid address or " +
                        "prefix; using the built-in set.";
                return null;
            }
            if (!IsInsideAllowedAllocation(prefix, allowedAllocations))
            {
                error = $"{FileName} line {lineNumber}: {prefix.Canonical} is outside every " +
                        "known Rockstar Online Services allocation and was refused; " +
                        "using the built-in set.";
                return null;
            }
            if (!seen.Add(prefix.Canonical))
            {
                error = $"{FileName} line {lineNumber}: {prefix.Canonical} is listed twice; " +
                        "using the built-in set.";
                return null;
            }
            prefixes.Add(prefix);
        }

        if (prefixes.Count == 0)
        {
            error = $"{FileName} contains no address; using the built-in set.";
            return null;
        }
        return prefixes;
    }

    private static bool IsInsideAllowedAllocation(
        IpPrefix prefix, IReadOnlyList<IpPrefix> allowedAllocations)
    {
        foreach (var allocation in allowedAllocations)
        {
            if (prefix.IsInside(allocation))
            {
                return true;
            }
        }
        return false;
    }
}
