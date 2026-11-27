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

            bool changed = !string.Equals(current.CurrentValue, proposed, StringComparison.OrdinalIgnoreCase);
            if (!changed && !includeUnchanged)
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

        if (proposedValues.TryGetValue("tcp.loopback_large_mtu", out string? loopbackMtu) &&
            string.Equals(loopbackMtu, "disable", StringComparison.OrdinalIgnoreCase) &&
            !knownIds.Contains("tcp.loopback_large_mtu"))
        {
            items.Add(new ChangePlanItem(
                ParameterId: "tcp.loopback_large_mtu",
                DisplayName: "Loopback Large MTU (Win11 24H2+ BBR2 Guard)",
                OldValue: "enable",
                NewValue: "disable",
                WindowsDefaultValue: "enable",
                ExactPath: "netsh int ipv4/ipv6 set global loopbacklargemtu=disable",
                SourceKind: ParameterSourceKind.Netsh,
                RequiresReboot: false));
        }

        return items;
    }
}
