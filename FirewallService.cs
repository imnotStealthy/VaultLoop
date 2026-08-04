using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ReplayGlitchGTA;

internal enum FirewallRuleState
{
    Inactive,
    Active,
    Invalid
}

internal sealed class FirewallService
{
    /// <summary>
    /// The single endpoint blocked by VaultLoop 1.2 and by the legacy script. Rules written
    /// before the address set was widened still carry it, and must keep being recognized so
    /// that upgrading while no-save is active cannot orphan a rule.
    /// </summary>
    private const string LegacyRemoteAddress = RockstarNetworks.ObservedSaveEndpoint;

    private const string RuleName = "VaultLoop - No Save";
    private const string PreviousRuleName = "Replay Glitch GTA V - No Save";
    private const string LegacyRuleName = "123456";
    private const string RuleMarker = "VaultLoop managed rule v2";
    private const string PreviousRuleDescription =
        "Blocks the Rockstar Cloud endpoint for GTA V no-save mode.";
    private const string RuleGrouping = "VaultLoop";
    private const int RuleDirectionOutbound = 2;
    private const int RuleActionBlock = 0;
    private const int ProtocolAny = 256;
    private const int ProfilesAll = int.MaxValue;
    private const int ModifyStateOk = 0;
    private const int FileNotFoundHResult = unchecked((int)0x80070002);
    private const int ConfirmationAttempts = 7;
    private const int InitialConfirmationDelayMilliseconds = 40;
    private const int MaximumConfirmationDelayMilliseconds = 640;
    private static readonly int[] KnownProfiles = [1, 2, 4];

    /// <summary>
    /// Serializes rule mutations on this instance.
    /// </summary>
    /// <remarks>
    /// The window applies a mutation on a thread-pool thread, so a close, a game-loss restore,
    /// or the exit path in <see cref="Program"/> can ask for a restore while one is still in
    /// flight. Two overlapping mutations would race over the same three rule names and make the
    /// confirmation step read the other one's outcome. <see cref="Monitor"/> is re-entrant,
    /// which the rollback inside <see cref="SetNoSaveEnabled"/> relies on. Reads stay outside:
    /// <see cref="GetState"/> is called from the polling thread and must never wait on a
    /// mutation.
    /// </remarks>
    private readonly object _mutationLock = new();

    internal FirewallRuleState GetState() => WithPolicy((policy, rules) =>
    {
        var current = InspectRule(rules, RuleName, requireCurrentShape: true);
        var previous = InspectRule(rules, PreviousRuleName, requireCurrentShape: false);
        var legacy = InspectRule(rules, LegacyRuleName, requireCurrentShape: false);
        if (current == RuleInspection.Missing &&
            previous == RuleInspection.Missing &&
            legacy == RuleInspection.Missing)
        {
            return FirewallRuleState.Inactive;
        }

        return current == RuleInspection.Exact &&
               previous == RuleInspection.Missing &&
               legacy == RuleInspection.Missing &&
               PolicyCanEnforce(policy)
            ? FirewallRuleState.Active
            : FirewallRuleState.Invalid;
    });

    internal void SetNoSaveEnabled(bool enabled, string? gameExecutablePath = null)
    {
        if (enabled &&
            (string.IsNullOrWhiteSpace(gameExecutablePath) ||
             !GameProcessService.IsTrustedGameExecutable(gameExecutablePath!)))
        {
            throw new InvalidOperationException(
                "A verified Rockstar GTA V process must be running before no-save can be enabled.");
        }

        lock (_mutationLock)
        {
            try
            {
                ApplyRuleMutation(enabled, gameExecutablePath);
                ConfirmState(enabled ? FirewallRuleState.Active : FirewallRuleState.Inactive);
            }
            catch (Exception enableException) when (enabled)
            {
                try
                {
                    SetNoSaveEnabled(false);
                }
                catch (Exception rollbackException)
                {
                    throw new InvalidOperationException(
                        "No-save activation failed and VaultLoop could not confirm its rollback. " +
                        "Use --restore before continuing.",
                        new AggregateException(enableException, rollbackException));
                }
                throw;
            }
        }
    }

    private static void ApplyRuleMutation(bool enabled, string? gameExecutablePath)
    {
        WithPolicy((policy, rules) =>
        {
            if (enabled && !PolicyCanEnforce(policy))
            {
                throw new InvalidOperationException(
                    "Windows Firewall is disabled or locked by policy.");
            }

            RemoveManagedRule(rules, RuleName);
            RemoveManagedRule(rules, PreviousRuleName);
            RemoveManagedRule(rules, LegacyRuleName);
            if (enabled)
            {
                AddManagedRule(rules, gameExecutablePath!);
            }
        });
    }

    private static void AddManagedRule(dynamic rules, string gameExecutablePath)
    {
        object? ruleObject = null;
        try
        {
            ruleObject = CreateComObject("HNetCfg.FWRule");
            dynamic rule = ruleObject;
            rule.Name = RuleName;
            rule.Description = RuleMarker;
            rule.Grouping = RuleGrouping;
            rule.Direction = RuleDirectionOutbound;
            rule.Action = RuleActionBlock;
            rule.Protocol = ProtocolAny;
            rule.LocalAddresses = "*";
            rule.RemoteAddresses = RockstarNetworks.FormatBlockedSet();
            rule.ApplicationName = Path.GetFullPath(gameExecutablePath);
            rule.Profiles = ProfilesAll;
            rule.InterfaceTypes = "All";
            rule.EdgeTraversal = false;
            rule.Enabled = true;
            rules.Add(rule);
        }
        finally
        {
            ReleaseComObject(ruleObject);
        }
    }

    /// <summary>
    /// Polls until the firewall reports the requested state, backing off between attempts.
    /// A single <see cref="GetState"/> costs roughly 15 ms, so the previous fixed 5 × 60 ms
    /// budget left barely 400 ms in total: under load — a full-screen game keeping the
    /// firewall service busy — that expired before Windows had published the change and
    /// rolled back a rule that had in fact been created.
    /// </summary>
    private void ConfirmState(FirewallRuleState expected)
    {
        var actual = FirewallRuleState.Invalid;
        var delay = InitialConfirmationDelayMilliseconds;
        for (var attempt = 0; attempt < ConfirmationAttempts; attempt++)
        {
            actual = GetState();
            if (actual == expected)
            {
                return;
            }
            if (attempt < ConfirmationAttempts - 1)
            {
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, MaximumConfirmationDelayMilliseconds);
            }
        }
        throw new InvalidOperationException(
            $"Windows Firewall did not reach the requested state. Current state: {actual}.");
    }

    private static RuleInspection InspectRule(
        dynamic rules, string name, bool requireCurrentShape)
    {
        object? ruleObject = null;
        try
        {
            ruleObject = rules.Item(name);
            dynamic rule = ruleObject;
            if (!requireCurrentShape)
            {
                return RuleInspection.Invalid;
            }

            return IsExactCurrentRule(rule)
                ? RuleInspection.Exact
                : RuleInspection.Invalid;
        }
        catch (Exception exception) when (exception.HResult == FileNotFoundHResult)
        {
            return RuleInspection.Missing;
        }
        finally
        {
            ReleaseComObject(ruleObject);
        }
    }

    private static bool IsExactCurrentRule(dynamic rule)
    {
        var applicationName = Convert.ToString(rule.ApplicationName)?.Trim().Trim('"') ?? "";
        return HasManagedBlockShape(rule) &&
               string.Equals(Convert.ToString(rule.Description), RuleMarker,
                   StringComparison.Ordinal) &&
               TargetsOnlyManagedAddresses(Convert.ToString(rule.RemoteAddresses) ?? "") &&
               GameProcessService.IsTrustedGameExecutable(applicationName);
    }

    /// <summary>
    /// The rule shape every VaultLoop rule shares, current or historical: an enabled outbound
    /// block over every protocol and profile, on all interfaces, with no edge traversal.
    /// What separates the variants is the marker, the addresses, and the application binding.
    /// </summary>
    private static bool HasManagedBlockShape(dynamic rule) =>
        (bool)rule.Enabled &&
        (int)rule.Direction == RuleDirectionOutbound &&
        (int)rule.Action == RuleActionBlock &&
        (int)rule.Protocol == ProtocolAny &&
        (int)rule.Profiles == ProfilesAll &&
        string.Equals(Convert.ToString(rule.LocalAddresses), "*",
            StringComparison.Ordinal) &&
        string.Equals(Convert.ToString(rule.InterfaceTypes), "All",
            StringComparison.OrdinalIgnoreCase) &&
        !(bool)rule.EdgeTraversal;

    private static void RemoveManagedRule(dynamic rules, string name)
    {
        object? ruleObject = null;
        try
        {
            ruleObject = rules.Item(name);
            if (IsOwnedManagedRule((dynamic)ruleObject, name))
            {
                rules.Remove(name);
            }
        }
        catch (Exception exception) when (exception.HResult == FileNotFoundHResult)
        {
        }
        finally
        {
            ReleaseComObject(ruleObject);
        }
    }

    private static bool IsOwnedManagedRule(dynamic rule, string name)
    {
        var description = Convert.ToString(rule.Description) ?? "";
        var grouping = Convert.ToString(rule.Grouping) ?? "";
        if (name == RuleName &&
            description.Equals(RuleMarker, StringComparison.Ordinal) &&
            grouping.Equals(RuleGrouping, StringComparison.Ordinal))
        {
            return true;
        }

        if (!IsExactHistoricalRule(rule))
        {
            return false;
        }

        var isPreviousApplicationRule =
            (name == RuleName || name == PreviousRuleName) &&
            ((description.Equals(PreviousRuleDescription, StringComparison.Ordinal) &&
              grouping.Equals(RuleGrouping, StringComparison.Ordinal)) ||
             (string.IsNullOrWhiteSpace(description) &&
              string.IsNullOrWhiteSpace(grouping)));
        var isLegacyScriptRule =
            name == LegacyRuleName &&
            string.IsNullOrWhiteSpace(description) &&
            string.IsNullOrWhiteSpace(grouping);
        return isPreviousApplicationRule || isLegacyScriptRule;
    }

    private static bool IsExactHistoricalRule(dynamic rule) =>
        HasManagedBlockShape(rule) &&
        string.IsNullOrWhiteSpace(Convert.ToString(rule.ApplicationName)) &&
        string.IsNullOrWhiteSpace(Convert.ToString(rule.ServiceName)) &&
        TargetsOnlyLegacyAddress(Convert.ToString(rule.RemoteAddresses) ?? "");

    private static bool PolicyCanEnforce(dynamic policy)
    {
        if ((int)policy.LocalPolicyModifyState != ModifyStateOk)
        {
            return false;
        }

        var activeProfiles = (int)policy.CurrentProfileTypes;
        if ((activeProfiles & 7) == 0)
        {
            return false;
        }

        foreach (var profile in KnownProfiles)
        {
            if ((activeProfiles & profile) != 0 && !(bool)policy.FirewallEnabled[profile])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// True when <paramref name="rawAddresses"/> describes exactly the managed block set —
    /// no extra entry, no missing entry, no broader prefix. Comparison runs on the canonical
    /// form because Windows Firewall rewrites what it is given (a /24 comes back as a dotted
    /// subnet mask, a bare address comes back with a full-length mask).
    /// </summary>
    internal static bool TargetsOnlyManagedAddresses(string rawAddresses)
    {
        var entries = rawAddresses.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries);
        var expected = RockstarNetworks.BlockedSet;
        if (entries.Length != expected.Count)
        {
            return false;
        }

        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var prefix = IpPrefix.TryParse(entry);
            if (prefix is null || !observed.Add(prefix.Canonical))
            {
                return false;
            }
        }

        foreach (var prefix in expected)
        {
            if (!observed.Remove(prefix.Canonical))
            {
                return false;
            }
        }
        return observed.Count == 0;
    }

    /// <summary>
    /// True when the rule still targets only the single endpoint used before the address set
    /// was widened. Used to identify removable rules left by earlier versions.
    /// </summary>
    internal static bool TargetsOnlyLegacyAddress(string rawAddresses)
    {
        var addresses = rawAddresses.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (addresses.Length != 1)
        {
            return false;
        }

        var address = addresses[0].Trim();
        return address.Equals(LegacyRemoteAddress, StringComparison.OrdinalIgnoreCase) ||
               address.Equals($"{LegacyRemoteAddress}/32", StringComparison.OrdinalIgnoreCase) ||
               address.Equals($"{LegacyRemoteAddress}/255.255.255.255",
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Opens the firewall policy and its rule collection, hands both to
    /// <paramref name="operation"/>, and releases them in reverse order whatever happens.
    /// Every COM object taken here is released here: a leaked reference keeps the firewall
    /// service alive and makes a later state read observe a stale rule collection.
    /// </summary>
    private static T WithPolicy<T>(Func<dynamic, dynamic, T> operation)
    {
        object? policyObject = null;
        object? rulesObject = null;
        try
        {
            policyObject = CreateComObject("HNetCfg.FwPolicy2");
            dynamic policy = policyObject;
            rulesObject = policy.Rules;
            return operation(policy, rulesObject);
        }
        finally
        {
            ReleaseComObject(rulesObject);
            ReleaseComObject(policyObject);
        }
    }

    private static void WithPolicy(Action<dynamic, dynamic> operation) =>
        WithPolicy<object?>((policy, rules) =>
        {
            operation(policy, rules);
            return null;
        });

    private static object CreateComObject(string programmaticId)
    {
        var type = Type.GetTypeFromProgID(programmaticId) ??
                   throw new PlatformNotSupportedException(
                       $"Windows Firewall COM component unavailable: {programmaticId}");
        return Activator.CreateInstance(type) ??
               throw new InvalidOperationException(
                   $"Unable to create Windows Firewall COM component: {programmaticId}");
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private enum RuleInspection
    {
        Missing,
        Exact,
        Invalid
    }
}
