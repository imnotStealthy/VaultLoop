using System;
using System.Collections.Generic;
using System.Net;

namespace ReplayGlitchGTA;

/// <summary>
/// Address sets used by the managed firewall rule and by the connection diagnostics.
/// </summary>
/// <remarks>
/// Every prefix below was read from ARIN RDAP on 2026-07-26 under the organization
/// "TAKE-TWO INTERACTIVE SOFTWARE, INC." (handle TTIS-4). Take-Two also holds Zynga and
/// corporate allocations; those are deliberately absent, and the configuration guard refuses
/// them, because blocking them would only produce collateral damage.
/// </remarks>
internal static class RockstarNetworks
{
    /// <summary>
    /// The endpoint originally observed in the game's own traffic. Retained so rules written
    /// by earlier VaultLoop versions and by the legacy script are still recognized and removed.
    /// </summary>
    internal const string ObservedSaveEndpoint = "192.81.241.171";

    /// <summary>
    /// The built-in blocked set, used when <c>endpoints.txt</c> is absent or unusable.
    /// </summary>
    /// <remarks>
    /// This is the single address the original AutoHotkey script blocked
    /// by the original AutoHotkey implementation, and the only configuration ever observed to be usable.
    ///
    /// It was briefly widened to <c>192.81.241.0/24</c> plus the IPv6 allocation. Field
    /// evidence retired that: the wider set reaches Rockstar authentication and the game drops
    /// the session mid-activity with "Unable to connect to Rockstar Games Services to
    /// authenticate", well before any save would occur. Only the wanted failure —
    /// "SAVING FAILED ... your progress will be saved when the connection is re-established" —
    /// leaves the session alive, and the neighbours inside that /24 do not produce it.
    ///
    /// Widen this only from measurement, never by assumption, and one address at a time.
    /// </remarks>
    internal static readonly string[] DefaultBlocked =
    [
        ObservedSaveEndpoint
    ];

    /// <summary>
    /// Rockstar Online Services allocations. Used to classify observed connections, and as the
    /// boundary within which a configured blocked set must stay.
    /// </summary>
    private static readonly (string Prefix, string Name)[] OnlineServices =
    [
        ("192.81.240.0/21", "RSONET-NA1"),
        ("104.255.104.0/22", "RSONET-NA2"),
        ("198.133.210.0/24", "RSONET-NA3"),
        ("164.153.136.0/22", "RSONET-NA4"),
        ("2620:11b:c000::/44", "V6-RSONET-NA")
    ];

    private static readonly IReadOnlyList<(IpPrefix Prefix, string Name)> OnlinePrefixes =
        ParseNamed(OnlineServices);

    private static readonly Lazy<BlockedConfiguration> Configuration =
        new(LoadConfiguration);

    /// <summary>
    /// The active blocked set. Resolved once per process: the firewall rule and the check that
    /// validates it must not disagree because the file changed while the application ran.
    /// </summary>
    internal static IReadOnlyList<IpPrefix> BlockedSet => Configuration.Value.Prefixes;

    /// <summary>Where the active set came from, for the diagnostics report.</summary>
    internal static string BlockedSource => Configuration.Value.Source;

    /// <summary>Why a present configuration file was refused, if it was.</summary>
    internal static string? BlockedConfigurationError => Configuration.Value.Error;

    /// <summary>The allocations a configured set is allowed to stay within.</summary>
    internal static IReadOnlyList<IpPrefix> OnlineServiceAllocations { get; } =
        ExtractPrefixes(OnlinePrefixes);

    /// <summary>
    /// The active set as the comma-separated canonical list the firewall rule carries. The
    /// rule and the check that validates it must be built from the same rendering.
    /// </summary>
    internal static string FormatBlockedSet()
    {
        var canonical = new List<string>();
        foreach (var prefix in BlockedSet)
        {
            canonical.Add(prefix.Canonical);
        }
        return string.Join(",", canonical);
    }

    internal static bool IsBlocked(IPAddress address)
    {
        foreach (var prefix in BlockedSet)
        {
            if (prefix.Contains(address))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns the Rockstar Online Services network that owns <paramref name="address"/>,
    /// or <c>null</c> when the address is outside every known allocation.
    /// </summary>
    internal static string? GetOnlineServiceName(IPAddress address)
    {
        foreach (var entry in OnlinePrefixes)
        {
            if (entry.Prefix.Contains(address))
            {
                return entry.Name;
            }
        }
        return null;
    }

    private static BlockedConfiguration LoadConfiguration()
    {
        var builtIn = Parse(DefaultBlocked);
        IReadOnlyList<IpPrefix>? configured;
        string? error;
        try
        {
            configured = BlockedEndpointsSettings.TryLoad(OnlineServiceAllocations, out error);
        }
        catch (Exception exception)
        {
            return new BlockedConfiguration(builtIn, "built-in default",
                $"the endpoint configuration could not be applied ({exception.Message}).");
        }

        return configured is null
            ? new BlockedConfiguration(builtIn, "built-in default", error)
            : new BlockedConfiguration(configured, BlockedEndpointsSettings.FileName, error);
    }

    private static IReadOnlyList<IpPrefix> ExtractPrefixes(
        IEnumerable<(IpPrefix Prefix, string Name)> entries)
    {
        var prefixes = new List<IpPrefix>();
        foreach (var entry in entries)
        {
            prefixes.Add(entry.Prefix);
        }
        return prefixes;
    }

    private static IReadOnlyList<IpPrefix> Parse(IEnumerable<string> values)
    {
        var prefixes = new List<IpPrefix>();
        foreach (var value in values)
        {
            prefixes.Add(IpPrefix.TryParse(value) ??
                         throw new InvalidOperationException($"Malformed network prefix: {value}"));
        }
        return prefixes;
    }

    private static IReadOnlyList<(IpPrefix, string)> ParseNamed(
        IEnumerable<(string Prefix, string Name)> values)
    {
        var prefixes = new List<(IpPrefix, string)>();
        foreach (var entry in values)
        {
            prefixes.Add((
                IpPrefix.TryParse(entry.Prefix) ??
                throw new InvalidOperationException($"Malformed network prefix: {entry.Prefix}"),
                entry.Name));
        }
        return prefixes;
    }

    private sealed class BlockedConfiguration
    {
        internal BlockedConfiguration(
            IReadOnlyList<IpPrefix> prefixes, string source, string? error)
        {
            Prefixes = prefixes;
            Source = source;
            Error = error;
        }

        internal IReadOnlyList<IpPrefix> Prefixes { get; }
        internal string Source { get; }
        internal string? Error { get; }
    }
}
