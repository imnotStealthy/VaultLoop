using System;
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
    internal const string RemoteAddress = "192.81.241.171";
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
    private static readonly int[] KnownProfiles = [1, 2, 4];

    internal FirewallRuleState GetState()
    {
        object? policyObject = null;
        object? rulesObject = null;
        try
        {
            policyObject = CreateComObject("HNetCfg.FwPolicy2");
            dynamic policy = policyObject;
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;

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
        }
        finally
        {
            ReleaseComObject(rulesObject);
            ReleaseComObject(policyObject);
        }
    }

    internal void SetNoSaveEnabled(bool enabled, string? gameExecutablePath = null)
    {
        if (enabled &&
            (string.IsNullOrWhiteSpace(gameExecutablePath) ||
             !GameProcessService.IsTrustedGameExecutable(gameExecutablePath!)))
        {
            throw new InvalidOperationException(
                "A verified Rockstar GTA V process must be running before no-save can be enabled.");
        }

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

    private static void ApplyRuleMutation(bool enabled, string? gameExecutablePath)
    {
        object? policyObject = null;
        object? rulesObject = null;
        object? ruleObject = null;
        try
        {
            policyObject = CreateComObject("HNetCfg.FwPolicy2");
            dynamic policy = policyObject;
            if (enabled && !PolicyCanEnforce(policy))
            {
                throw new InvalidOperationException(
                    "Windows Firewall is disabled or locked by policy.");
            }
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;

            RemoveManagedRule(rules, RuleName);
            RemoveManagedRule(rules, PreviousRuleName);
            RemoveManagedRule(rules, LegacyRuleName);
            if (enabled)
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
                rule.RemoteAddresses = RemoteAddress;
                rule.ApplicationName = Path.GetFullPath(gameExecutablePath!);
                rule.Profiles = ProfilesAll;
                rule.InterfaceTypes = "All";
                rule.EdgeTraversal = false;
                rule.Enabled = true;
                rules.Add(rule);
            }
        }
        finally
        {
            ReleaseComObject(ruleObject);
            ReleaseComObject(rulesObject);
            ReleaseComObject(policyObject);
        }
    }

    private void ConfirmState(FirewallRuleState expected)
    {
        FirewallRuleState actual = FirewallRuleState.Invalid;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            actual = GetState();
            if (actual == expected)
            {
                return;
            }
            Thread.Sleep(60);
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
        return (bool)rule.Enabled &&
               (int)rule.Direction == RuleDirectionOutbound &&
               (int)rule.Action == RuleActionBlock &&
               (int)rule.Protocol == ProtocolAny &&
               (int)rule.Profiles == ProfilesAll &&
               string.Equals(Convert.ToString(rule.LocalAddresses), "*",
                   StringComparison.Ordinal) &&
               string.Equals(Convert.ToString(rule.InterfaceTypes), "All",
                   StringComparison.OrdinalIgnoreCase) &&
               !(bool)rule.EdgeTraversal &&
               string.Equals(Convert.ToString(rule.Description), RuleMarker,
                   StringComparison.Ordinal) &&
               TargetsOnlyRemoteAddress(Convert.ToString(rule.RemoteAddresses) ?? "") &&
               GameProcessService.IsTrustedGameExecutable(applicationName);
    }

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
        (bool)rule.Enabled &&
        (int)rule.Direction == RuleDirectionOutbound &&
        (int)rule.Action == RuleActionBlock &&
        (int)rule.Protocol == ProtocolAny &&
        (int)rule.Profiles == ProfilesAll &&
        string.Equals(Convert.ToString(rule.LocalAddresses), "*",
            StringComparison.Ordinal) &&
        string.Equals(Convert.ToString(rule.InterfaceTypes), "All",
            StringComparison.OrdinalIgnoreCase) &&
        !(bool)rule.EdgeTraversal &&
        string.IsNullOrWhiteSpace(Convert.ToString(rule.ApplicationName)) &&
        string.IsNullOrWhiteSpace(Convert.ToString(rule.ServiceName)) &&
        TargetsOnlyRemoteAddress(Convert.ToString(rule.RemoteAddresses) ?? "");

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

    private static bool TargetsOnlyRemoteAddress(string rawAddresses)
    {
        var addresses = rawAddresses.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (addresses.Length != 1)
        {
            return false;
        }

        var address = addresses[0].Trim();
        return address.Equals(RemoteAddress, StringComparison.OrdinalIgnoreCase) ||
               address.Equals($"{RemoteAddress}/32", StringComparison.OrdinalIgnoreCase) ||
               address.Equals($"{RemoteAddress}/255.255.255.255",
                   StringComparison.OrdinalIgnoreCase);
    }

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
