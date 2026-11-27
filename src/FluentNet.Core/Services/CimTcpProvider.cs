using FluentNet.Core.Models;
#if NET8_0_OR_GREATER
using Microsoft.Management.Infrastructure;
#else
using System.Management;
#endif

namespace FluentNet.Core.Services;

public sealed class CimTcpProvider
{
    private const string StandardCimNamespace = @"root\StandardCimv2";

    public async Task<(IReadOnlyList<NetworkParameterState> Parameters, IReadOnlyDictionary<string, IReadOnlyList<AdapterAdvancedPropertyInfo>> AdapterAdvanced)> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        Task<IReadOnlyList<NetworkParameterState>> tcpTask = Task.Run(() => ReadNetTcpSettings(cancellationToken), cancellationToken);
        Task<IReadOnlyList<NetworkParameterState>> offloadTask = Task.Run(() => ReadOffloadGlobalSettings(cancellationToken), cancellationToken);
        Task<IReadOnlyDictionary<string, IReadOnlyList<AdapterAdvancedPropertyInfo>>> advTask = Task.Run(() => ReadAdapterAdvancedProperties(cancellationToken), cancellationToken);

        await Task.WhenAll(tcpTask, offloadTask, advTask).ConfigureAwait(false);

        var combined = new List<NetworkParameterState>(tcpTask.Result.Count + offloadTask.Result.Count);
        if (tcpTask.Result.Count > 0)
        {
            combined.AddRange(tcpTask.Result);
        }
        else
        {
            combined.AddRange(ReadNetshFallback(cancellationToken));
        }

        combined.AddRange(offloadTask.Result);

        return (combined, advTask.Result);
    }

    private static IReadOnlyList<NetworkParameterState> ReadNetshFallback(CancellationToken cancellationToken)
    {
        var list = new List<NetworkParameterState>();
        try
        {
            string globalOut = RunNetsh("int tcp show global", cancellationToken);
            string suppOut = RunNetsh("int tcp show supplemental", cancellationToken);

            AddCimParam(list, "tcp.autotuning", "TCP Window Auto-Tuning", "General", TitleCase(ExtractNetshField(globalOut, "Receive Window Auto-Tuning Level") ?? "normal"), "Normal", "netsh int tcp set global autotuninglevel=", false);
            AddCimParam(list, "tcp.heuristics", "Scaling Heuristics", "General", TitleCase(ExtractNetshField(globalOut, "Receive-Side Scaling State") is not null ? (ExtractNetshField(RunNetsh("int tcp show heuristics", cancellationToken), "Window Scaling Heuristics") ?? "disabled") : "disabled"), "Disabled", "netsh int tcp set heuristics", false);
            AddCimParam(list, "tcp.congestion", "Congestion Control Provider", "General", (ExtractNetshField(suppOut, "Congestion Control Provider") ?? ExtractNetshField(globalOut, "Add-On Congestion Control Provider") ?? "cubic").ToUpperInvariant(), "CUBIC", "netsh int tcp set supplemental template=Internet congestionprovider=", false);
            AddCimParam(list, "tcp.ecn", "ECN Capability", "General", TitleCase(ExtractNetshField(globalOut, "ECN Capability") ?? "disabled"), "Default", "netsh int tcp set global ecncapability=", false);
            AddCimParam(list, "tcp.timestamps", "TCP 1323 Timestamps", "General", TitleCase(ExtractNetshField(globalOut, "Timestamps") ?? "disabled"), "Disabled", "netsh int tcp set global timestamps=", false);
            AddCimParam(list, "offload.rss", "Receive-Side Scaling (RSS)", "General", TitleCase(ExtractNetshField(globalOut, "Receive-Side Scaling State") ?? "enabled"), "Enabled", "MSFT_NetOffloadGlobalSetting.ReceiveSideScaling", false);
            AddCimParam(list, "offload.rsc", "Receive Segment Coalescing (RSC)", "General", TitleCase(ExtractNetshField(globalOut, "Receive Segment Coalescing State") ?? "enabled"), "Enabled", "MSFT_NetOffloadGlobalSetting.ReceiveSegmentCoalescing", false);
            AddCimParam(list, "tcp.maxsynretx", "Max SYN Retransmissions", "Advanced", ExtractNetshField(globalOut, "Max SYN Retransmissions") ?? "4", "4", "MSFT_NetTCPSetting[Internet].MaxSynRetransmissions", false);
            AddCimParam(list, "tcp.nonsackrtt", "NonSack Rtt Resiliency", "Advanced", TitleCase(ExtractNetshField(globalOut, "Non Sack Rtt Resiliency") ?? "disabled"), "Disabled", "MSFT_NetTCPSetting[Internet].NonSackRttResiliency", false);
            AddCimParam(list, "tcp.initialrto", "Initial RTO (ms)", "Advanced", ExtractNetshField(globalOut, "Initial RTO") ?? "3000", "3000", "MSFT_NetTCPSetting[Internet].InitialRtoMs", false);
            AddCimParam(list, "tcp.minrto", "Min RTO (ms)", "Advanced", ExtractNetshField(suppOut, "Min RTO") ?? "300", "300", "MSFT_NetTCPSetting[Internet].MinRtoMs", false);
        }
        catch
        {
            // Ignore fallback error
        }

        return list;
    }

    private static string RunNetsh(string args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        });
        if (proc is null) return string.Empty;
        string text = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(3000);
        return text;
    }

    private static string? ExtractNetshField(string output, string label)
    {
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string left = line.Substring(0, colon).Trim();
            if (left.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return line.Substring(colon + 1).Trim();
            }
        }
        return null;
    }

    private static string TitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        string lower = value.Trim().ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower.Substring(1);
    }

    private static IReadOnlyList<NetworkParameterState> ReadNetTcpSettings(CancellationToken cancellationToken)
    {
        var list = new List<NetworkParameterState>();
        try
        {
            foreach (Dictionary<string, string> row in QueryCim("SELECT SettingName, AutoTuningLevelLocal, ScalingHeuristics, CongestionProvider, EcnCapability, Timestamps, MaxSynRetransmissions, NonSackRttResiliency, InitialRtoMs, MinRtoMs, InitialCongestionWindowMss FROM MSFT_NetTCPSetting WHERE SettingName='Internet'", cancellationToken))
            {
                string template = row.TryGetValue("SettingName", out string? s) ? s : "Internet";

                AddCimParam(list, "tcp.autotuning", "TCP Window Auto-Tuning", "General", DecodeAutoTuning(Get(row, "AutoTuningLevelLocal")), "Normal", "netsh int tcp set global autotuninglevel=", false);
                AddCimParam(list, "tcp.heuristics", "Scaling Heuristics", "General", DecodeEnabledDisabled(Get(row, "ScalingHeuristics")), "Disabled", "netsh int tcp set heuristics", false);
                AddCimParam(list, "tcp.congestion", "Congestion Control Provider", "General", DecodeCongestion(Get(row, "CongestionProvider")), "CUBIC", $"netsh int tcp set supplemental template={template} congestionprovider=", false);
                AddCimParam(list, "tcp.ecn", "ECN Capability", "General", DecodeDefaultEnabledDisabled(Get(row, "EcnCapability")), "Default", "netsh int tcp set global ecncapability=", false);
                AddCimParam(list, "tcp.timestamps", "TCP 1323 Timestamps", "General", DecodeDefaultEnabledDisabled(Get(row, "Timestamps")), "Disabled", "netsh int tcp set global timestamps=", false);
                AddCimParam(list, "tcp.maxsynretx", "Max SYN Retransmissions", "Advanced", Get(row, "MaxSynRetransmissions"), "4", $"MSFT_NetTCPSetting[{template}].MaxSynRetransmissions", false);
                AddCimParam(list, "tcp.nonsackrtt", "NonSack Rtt Resiliency", "Advanced", DecodeEnabledDisabled(Get(row, "NonSackRttResiliency")), "Disabled", $"MSFT_NetTCPSetting[{template}].NonSackRttResiliency", false);
                AddCimParam(list, "tcp.initialrto", "Initial RTO (ms)", "Advanced", Get(row, "InitialRtoMs"), "3000", $"MSFT_NetTCPSetting[{template}].InitialRtoMs", false);
                AddCimParam(list, "tcp.minrto", "Min RTO (ms)", "Advanced", Get(row, "MinRtoMs"), "300", $"MSFT_NetTCPSetting[{template}].MinRtoMs", false);
                AddCimParam(list, "tcp.icw", "Initial Congestion Window (MSS)", "Advanced", Get(row, "InitialCongestionWindowMss"), "10", $"netsh int tcp set supplemental template={template} icw=", false);
            }
        }
        catch
        {
            // CIM provider unavailable in restricted/non-Windows sandbox
        }

        return list;
    }

    private static IReadOnlyList<NetworkParameterState> ReadOffloadGlobalSettings(CancellationToken cancellationToken)
    {
        var list = new List<NetworkParameterState>();
        try
        {
            foreach (Dictionary<string, string> row in QueryCim("SELECT ReceiveSideScaling, ReceiveSegmentCoalescing FROM MSFT_NetOffloadGlobalSetting", cancellationToken))
            {
                AddCimParam(list, "offload.rss", "Receive-Side Scaling (RSS)", "General", DecodeEnabledDisabled(Get(row, "ReceiveSideScaling")), "Enabled", "MSFT_NetOffloadGlobalSetting.ReceiveSideScaling", false);
                AddCimParam(list, "offload.rsc", "Receive Segment Coalescing (RSC)", "General", DecodeEnabledDisabled(Get(row, "ReceiveSegmentCoalescing")), "Enabled", "MSFT_NetOffloadGlobalSetting.ReceiveSegmentCoalescing", false);
            }
        }
        catch
        {
            // Ignore if CIM class unavailable
        }

        return list;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<AdapterAdvancedPropertyInfo>> ReadAdapterAdvancedProperties(CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, List<AdapterAdvancedPropertyInfo>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (Dictionary<string, string> row in QueryCim("SELECT Name, RegistryKeyword, DisplayName, DisplayValue, RegistryValue FROM MSFT_NetAdapterAdvancedPropertySettingData", cancellationToken))
            {
                if (!row.TryGetValue("Name", out string? alias) || string.IsNullOrWhiteSpace(alias))
                {
                    continue;
                }

                string keyword = Get(row, "RegistryKeyword");
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    continue;
                }

                if (!map.TryGetValue(alias, out List<AdapterAdvancedPropertyInfo>? list))
                {
                    list = new List<AdapterAdvancedPropertyInfo>();
                    map[alias] = list;
                }

                list.Add(new AdapterAdvancedPropertyInfo(
                    RegistryKeyword: keyword,
                    DisplayName: Get(row, "DisplayName") is { Length: > 0 } dn ? dn : keyword,
                    DisplayValue: Get(row, "DisplayValue"),
                    RegistryValue: Get(row, "RegistryValue")));
            }
        }
        catch
        {
            // Ignore if CIM class unavailable
        }

        return map.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<AdapterAdvancedPropertyInfo>)kvp.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string Get(Dictionary<string, string> row, string key)
        => row.TryGetValue(key, out string? v) ? v : string.Empty;

    public static string DecodeEnabledDisabled(string raw) => raw.Trim() switch
    {
        "0" => "Disabled",
        "1" => "Enabled",
        "2" => "Default",
        _ => raw
    };

    private static string DecodeDefaultEnabledDisabled(string raw) => raw.Trim() switch
    {
        "0" => "Disabled",
        "1" => "Enabled",
        "2" => "Default",
        _ => raw
    };

    public static string DecodeAutoTuning(string raw) => raw.Trim() switch
    {
        "0" => "Disabled",
        "1" => "HighlyRestricted",
        "2" => "Restricted",
        "3" => "Normal",
        "4" => "Experimental",
        _ => raw
    };

    public static string DecodeCongestion(string raw) => raw.Trim() switch
    {
        "0" => "Default",
        "2" => "CTCP",
        "4" => "DCTCP",
        "5" => "CUBIC",
        "6" => "BBR2",
        _ => raw
    };

    private static void AddCimParam(
        List<NetworkParameterState> list,
        string id,
        string displayName,
        string category,
        string value,
        string defaultValue,
        string exactPath,
        bool requiresReboot)
    {
        bool found = !string.IsNullOrWhiteSpace(value);
        list.Add(new NetworkParameterState(
            Id: id,
            DisplayName: displayName,
            Category: category,
            CurrentValue: found ? value : "NotSet",
            WindowsDefaultValue: defaultValue,
            ExactPath: exactPath,
            SourceKind: ParameterSourceKind.Cim,
            SupportStatus: found ? ParameterSupportStatus.Supported : ParameterSupportStatus.NotSet,
            RequiresReboot: requiresReboot));
    }

    private static IEnumerable<Dictionary<string, string>> QueryCim(string wqlQuery, CancellationToken cancellationToken)
    {
#if NET8_0_OR_GREATER
        using CimSession session = CimSession.Create(null);
        foreach (CimInstance instance in session.QueryInstances(StandardCimNamespace, "WQL", wqlQuery))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (CimProperty prop in instance.CimInstanceProperties)
            {
                dict[prop.Name] = FormatValue(prop.Value);
            }
            yield return dict;
        }
#else
        using var searcher = new ManagementObjectSearcher(StandardCimNamespace, wqlQuery);
        foreach (ManagementBaseObject obj in searcher.Get())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (PropertyData prop in obj.Properties)
            {
                dict[prop.Name] = FormatValue(prop.Value);
            }
            yield return dict;
        }
#endif
    }

    private static string FormatValue(object? val)
    {
        if (val is null)
        {
            return string.Empty;
        }

        if (val is Array arr)
        {
            return string.Join(",", arr.Cast<object>().Select(x => x?.ToString() ?? string.Empty));
        }

        return val.ToString() ?? string.Empty;
    }
}
