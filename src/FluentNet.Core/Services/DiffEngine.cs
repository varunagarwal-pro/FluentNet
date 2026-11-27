using FluentNet.Core.Models;

namespace FluentNet.Core.Services;

public sealed class DiffEngine
{
    public IReadOnlyList<ChangePlanItem> ComputeDiff(
        HostScanReport report,
        IReadOnlyDictionary<string, string> proposedValues,
        bool includeUnchanged = false)
    {
        var items = new List<ChangePlanItem>();
        var knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (NetworkParameterState current in report.Parameters)
        {
            knownIds.Add(current.Id);
            if (!proposedValues.TryGetValue(current.Id, out string? proposed))
            {
                continue;
            }

            // Never queue diffs for unsupported or unreadable CIM parameters
            if (current.SourceKind == ParameterSourceKind.Cim &&
                (current.SupportStatus == ParameterSupportStatus.NotSet ||
                 current.SupportStatus == ParameterSupportStatus.UnsupportedOnOs ||
                 current.SupportStatus == ParameterSupportStatus.ReadOnlyOnOs))
            {
                continue;
            }

            bool equivalent = ValuesAreEquivalent(current, proposed);
            if (equivalent && !includeUnchanged)
            {
                continue;
            }

            items.Add(new ChangePlanItem(
                ParameterId: current.Id,
                DisplayName: current.DisplayName,
                OldValue: current.CurrentValue,
                NewValue: proposed,
                WindowsDefaultValue: current.WindowsDefaultValue,
                ExactPath: current.ExactPath,
                SourceKind: current.SourceKind,
                RequiresReboot: current.RequiresReboot));
        }

        // Only synthesize loopback_large_mtu if HostScanner couldn't read it AND BBR2 guard requires disabling it
        if (proposedValues.TryGetValue("tcp.loopback_large_mtu", out string? loopbackMtu) &&
            (string.Equals(loopbackMtu, "Disabled", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(loopbackMtu, "disable", StringComparison.OrdinalIgnoreCase)) &&
            !knownIds.Contains("tcp.loopback_large_mtu"))
        {
            items.Add(new ChangePlanItem(
                ParameterId: "tcp.loopback_large_mtu",
                DisplayName: "Loopback Large MTU (Win11 24H2+ BBR2 Guard)",
                OldValue: "Enabled",
                NewValue: "Disabled",
                WindowsDefaultValue: "Enabled",
                ExactPath: "MSFT_NetIPv4Protocol.LoopbackLargeMtu",
                SourceKind: ParameterSourceKind.Netsh,
                RequiresReboot: false));
        }

        return items;
    }

    public static bool ValuesAreEquivalent(NetworkParameterState current, string proposed)
    {
        string cur = current.CurrentValue.Trim();
        string prop = proposed.Trim();

        if (string.Equals(cur, prop, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Normalize enable/enabled and disable/disabled
        if (IsEnabledToken(cur) && IsEnabledToken(prop)) return true;
        if (IsDisabledToken(cur) && IsDisabledToken(prop)) return true;

        // Normalize hex vs decimal DWORD values (e.g., 0x0000000a == 10, 0xffffffff == 4294967295)
        if (ApplyEngine.TryParseDword(cur, out int curNum) &&
            ApplyEngine.TryParseDword(prop, out int propNum) &&
            curNum == propNum)
        {
            return true;
        }

        // For Registry parameters, an absent key ("NotSet") IS the Windows default.
        // So if Current is NotSet and Proposed equals WindowsDefaultValue (or NotSet), no write is needed.
        if (current.SourceKind == ParameterSourceKind.Registry)
        {
            bool curIsNotSet = string.Equals(cur, "NotSet", StringComparison.OrdinalIgnoreCase);
            bool propIsNotSet = string.Equals(prop, "NotSet", StringComparison.OrdinalIgnoreCase);

            if (curIsNotSet && (propIsNotSet || MatchesDefault(prop, current.WindowsDefaultValue)))
            {
                return true;
            }
        }

        // ECN / Timestamps: on Windows 10/11, "Default" resolves to "Disabled" in live kernel state
        if (current.Id is "tcp.ecn" or "tcp.timestamps")
        {
            bool curOff = IsDisabledToken(cur) || string.Equals(cur, "Default", StringComparison.OrdinalIgnoreCase);
            bool propOff = IsDisabledToken(prop) || string.Equals(prop, "Default", StringComparison.OrdinalIgnoreCase);
            if (curOff && propOff)
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesDefault(string proposed, string windowsDefault)
    {
        if (string.Equals(proposed, windowsDefault, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ApplyEngine.TryParseDword(proposed, out int p) &&
            ApplyEngine.TryParseDword(windowsDefault, out int d) &&
            p == d)
        {
            return true;
        }

        return false;
    }

    private static bool IsEnabledToken(string s) =>
        s.Equals("Enabled", StringComparison.OrdinalIgnoreCase) || s.Equals("enable", StringComparison.OrdinalIgnoreCase);

    private static bool IsDisabledToken(string s) =>
        s.Equals("Disabled", StringComparison.OrdinalIgnoreCase) || s.Equals("disable", StringComparison.OrdinalIgnoreCase);
}
