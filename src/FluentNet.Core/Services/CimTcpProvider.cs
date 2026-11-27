using FluentNet.Core.Models;
#if NET8_0_OR_GREATER
using Microsoft.Management.Infrastructure;
#else
using System.Management;
#endif

namespace FluentNet.Core.Services;

public sealed class CimTcpProvider
{
    public const string StandardCimNamespace = @"root\StandardCimv2";

    public async Task<(IReadOnlyList<NetworkParameterState> Parameters, IReadOnlyDictionary<string, IReadOnlyList<AdapterAdvancedPropertyInfo>> AdapterAdvanced)> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        Task<IReadOnlyList<NetworkParameterState>> tcpTask = Task.Run(() => ReadNetTcpSettings(cancellationToken), cancellationToken);
        Task<IReadOnlyList<NetworkParameterState>> offloadTask = Task.Run(() => ReadOffloadGlobalSettings(cancellationToken), cancellationToken);
        Task<NetworkParameterState?> loopbackTask = Task.Run(() => ReadLoopbackLargeMtu(cancellationToken), cancellationToken);
        Task<IReadOnlyDictionary<string, IReadOnlyList<AdapterAdvancedPropertyInfo>>> advTask = Task.Run(() => ReadAdapterAdvancedProperties(cancellationToken), cancellationToken);

        await Task.WhenAll(tcpTask, offloadTask, loopbackTask, advTask).ConfigureAwait(false);

        var combined = new List<NetworkParameterState>(tcpTask.Result.Count + offloadTask.Result.Count + 2);
        if (tcpTask.Result.Count > 0)
        {
            combined.AddRange(tcpTask.Result);
        }
        else
        {
            combined.AddRange(ReadNetshFallback(cancellationToken));
        }

        combined.AddRange(offloadTask.Result);

        if (loopbackTask.Result is not null)
        {
            combined.Add(loopbackTask.Result);
        }

        return (combined, advTask.Result);
    }

    private static NetworkParameterState? ReadLoopbackLargeMtu(CancellationToken cancellationToken)
    {
        try
        {
            string ipv4Global = RunNetsh("int ipv4 show global", cancellationToken);
            string? val = ExtractNetshField(ipv4Global, "Loopback Large MTU");
            if (val is not null && !string.IsNullOrWhiteSpace(val))
            {
                string normalized = val.Trim().StartsWith("dis", StringComparison.OrdinalIgnoreCase) ? "Disabled" : "Enabled";
                return new NetworkParameterState(
                    Id: "tcp.loopback_large_mtu",
                    DisplayName: "Loopback Large MTU (BBR2 Guard)",
                    Category: "General",
                    CurrentValue: normalized,
                    WindowsDefaultValue: "Enabled",
                    ExactPath: "MSFT_NetIPv4Protocol.LoopbackLargeMtu",
                    SourceKind: ParameterSourceKind.Netsh,
                    SupportStatus: ParameterSupportStatus.Supported,
                    RequiresReboot: false,
                    Description: "Disable on Win11 24H2+ when BBR2 is active to prevent localhost stalls");
            }
        }
        catch
        {
            // Ignore when netsh unavailable in non-Windows test runner
        }

        return null;
    }

    private static IReadOnlyList<NetworkParameterState> ReadNetshFallback(CancellationToken cancellationToken)
    {
        var list = new List<NetworkParameterState>();
        try
        {
            string globalOut = RunNetsh("int tcp show global", cancellationToken);
            string suppOut = RunNetsh("int tcp show supplemental", cancellationToken);
            string heurOut = RunNetsh("int tcp show heuristics", cancellationToken);

            AddCimParam(list, "tcp.autotuning", "TCP Window Auto-Tuning", "General",
                NormalizeAutoTuning(ExtractNetshField(globalOut, "Receive Window Auto-Tuning Level") ?? "Normal"),
                "Normal", "MSFT_NetTCPSetting[Internet].AutoTuningLevelLocal", false,
                "Dynamically scales TCP receive window for high-BDP links");

            AddCimParam(list, "tcp.heuristics", "Scaling Heuristics", "General",
                DecodeEnabledDisabled(ExtractNetshField(heurOut, "Window Scaling Heuristics") ?? "Disabled"),
                "Disabled", "MSFT_NetTCPSetting[Internet].ScalingHeuristics", false,
                "Disable so Windows won't silently downgrade auto-tuning");

            AddCimParam(list, "tcp.congestion", "Congestion Control Provider", "General",
                DecodeCongestion(ExtractNetshField(suppOut, "Congestion Control Provider") ?? ExtractNetshField(globalOut, "Add-On Congestion Control Provider") ?? "CUBIC"),
                "CUBIC", "MSFT_NetTCPSetting[Internet].CongestionProvider", false,
                "CUBIC standard; BBR2 enables Win11 24H2+ loopback guard");

            AddCimParam(list, "tcp.ecn", "ECN Capability", "General",
                DecodeDefaultEnabledDisabled(ExtractNetshField(globalOut, "ECN Capability") ?? "Disabled"),
                "Disabled", "MSFT_NetTCPSetting[Internet].EcnCapability", false,
                "Explicit Congestion Notification (RFC 3168)");

            AddCimParam(list, "tcp.timestamps", "TCP 1323 Timestamps", "General",
                DecodeDefaultEnabledDisabled(ExtractNetshField(globalOut, "Timestamps") ?? "Disabled"),
                "Disabled", "MSFT_NetTCPSetting[Internet].Timestamps", false,
                "Disable to save 12 bytes per packet header overhead");

            string? tfo = ExtractNetshField(globalOut, "TCP Fast Open");
            if (tfo is not null && !string.IsNullOrWhiteSpace(tfo))
            {
                AddCimParam(list, "tcp.fastopen", "TCP Fast Open (TFO)", "General",
                    DecodeEnabledDisabled(tfo), "Enabled",
                    "MSFT_NetTCPSetting[Internet].FastOpen", false,
                    "Reduces TCP handshake latency by sending data in SYN packets");
            }

            AddCimParam(list, "offload.rss", "Receive-Side Scaling (RSS)", "General",
                DecodeEnabledDisabled(ExtractNetshField(globalOut, "Receive-Side Scaling State") ?? "Enabled"),
                "Enabled", "MSFT_NetOffloadGlobalSetting.ReceiveSideScaling", false,
                "Distributes packet processing across multiple CPU cores");

            AddCimParam(list, "offload.rsc", "Receive Segment Coalescing (RSC)", "General",
                DecodeEnabledDisabled(ExtractNetshField(globalOut, "Receive Segment Coalescing State") ?? "Enabled"),
                "Enabled", "MSFT_NetOffloadGlobalSetting.ReceiveSegmentCoalescing", false,
                "Coalesces incoming TCP segments; disable in Gaming mode to lower jitter");

            AddCimParam(list, "tcp.maxsynretx", "Max SYN Retransmissions", "Advanced",
                ExtractNetshField(globalOut, "Max SYN Retransmissions") ?? "4",
                "4", "MSFT_NetTCPSetting[Internet].MaxSynRetransmissions", false,
                "Retries before failing unreachable handshake (2 optimal)");

            AddCimParam(list, "tcp.nonsackrtt", "NonSack Rtt Resiliency", "Advanced",
                DecodeEnabledDisabled(ExtractNetshField(globalOut, "Non Sack Rtt Resiliency") ?? "Disabled"),
                "Disabled", "MSFT_NetTCPSetting[Internet].NonSackRttResiliency", false,
                "Keep Disabled on modern SACK-capable networks");

            AddCimParam(list, "tcp.initialrto", "Initial RTO (ms)", "Advanced",
                ExtractNetshField(globalOut, "Initial RTO") ?? "3000",
                "3000", "MSFT_NetTCPSetting[Internet].InitialRtoMs", false,
                "Initial retransmission timeout (2000 optimal, 1000 gaming)");

            AddCimParam(list, "tcp.minrto", "Min RTO (ms)", "Advanced",
                ExtractNetshField(suppOut, "Min RTO") ?? "300",
                "300", "MSFT_NetTCPSetting[Internet].MinRtoMs", false,
                "Minimum retransmission floor (300ms safe baseline)");
        }
        catch
        {
            // Ignore fallback error in non-Windows environment
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

    private static IReadOnlyList<NetworkParameterState> ReadNetTcpSettings(CancellationToken cancellationToken)
    {
        var list = new List<NetworkParameterState>();
        try
        {
            // Use SELECT * so builds missing optional properties (e.g. FastOpen/HyStart/Prr) never throw WBEM_E_INVALID_QUERY.
            const string wql = "SELECT * FROM MSFT_NetTCPSetting WHERE SettingName='Internet'";
            foreach (Dictionary<string, string> row in QueryCim(wql, cancellationToken))
            {
                string template = row.TryGetValue("SettingName", out string? s) && !string.IsNullOrWhiteSpace(s) ? s : "Internet";

                AddCimParam(list, "tcp.autotuning", "TCP Window Auto-Tuning", "General",
                    DecodeAutoTuning(Get(row, "AutoTuningLevelLocal")), "Normal",
                    $"MSFT_NetTCPSetting[{template}].AutoTuningLevelLocal", false,
                    "Dynamically scales TCP receive window for high-BDP links");

                AddCimParam(list, "tcp.heuristics", "Scaling Heuristics", "General",
                    DecodeEnabledDisabled(Get(row, "ScalingHeuristics")), "Disabled",
                    $"MSFT_NetTCPSetting[{template}].ScalingHeuristics", false,
                    "Disable so Windows won't silently downgrade auto-tuning");

                AddCimParam(list, "tcp.congestion", "Congestion Control Provider", "General",
                    DecodeCongestion(Get(row, "CongestionProvider")), "CUBIC",
                    $"MSFT_NetTCPSetting[{template}].CongestionProvider", false,
                    "CUBIC standard; BBR2 enables Win11 24H2+ loopback guard");

                AddCimParam(list, "tcp.ecn", "ECN Capability", "General",
                    DecodeDefaultEnabledDisabled(Get(row, "EcnCapability")), "Disabled",
                    $"MSFT_NetTCPSetting[{template}].EcnCapability", false,
                    "Explicit Congestion Notification (RFC 3168)");

                AddCimParam(list, "tcp.timestamps", "TCP 1323 Timestamps", "General",
                    DecodeDefaultEnabledDisabled(Get(row, "Timestamps")), "Disabled",
                    $"MSFT_NetTCPSetting[{template}].Timestamps", false,
                    "Disable to save 12 bytes per packet header overhead");

                // Optional modern Win10/11 properties: only register if present on this OS build
                string tfoRaw = Get(row, "FastOpen");
                if (!string.IsNullOrWhiteSpace(tfoRaw))
                {
                    AddCimParam(list, "tcp.fastopen", "TCP Fast Open (TFO)", "General",
                        DecodeEnabledDisabled(tfoRaw), "Enabled",
                        $"MSFT_NetTCPSetting[{template}].FastOpen", false,
                        "Reduces TCP handshake latency by sending data in SYN packets");
                }

                string pacingRaw = Get(row, "PacingProfile");
                if (!string.IsNullOrWhiteSpace(pacingRaw))
                {
                    AddCimParam(list, "tcp.pacing", "TCP Pacing Profile", "Advanced",
                        DecodePacingProfile(pacingRaw), "Off",
                        $"MSFT_NetTCPSetting[{template}].PacingProfile", false,
                        "Paces outbound packet bursts to reduce micro-bufferbloat");
                }

                string hystartRaw = Get(row, "HyStart");
                if (!string.IsNullOrWhiteSpace(hystartRaw))
                {
                    AddCimParam(list, "tcp.hystart", "Hybrid Slow Start (HyStart)", "Advanced",
                        DecodeEnabledDisabled(hystartRaw), "Enabled",
                        $"MSFT_NetTCPSetting[{template}].HyStart", false,
                        "Detects safe slow-start exit point to prevent packet loss spikes");
                }

                string prrRaw = Get(row, "Prr");
                if (!string.IsNullOrWhiteSpace(prrRaw))
                {
                    AddCimParam(list, "tcp.prr", "Proportional Rate Reduction (PRR)", "Advanced",
                        DecodeEnabledDisabled(prrRaw), "Enabled",
                        $"MSFT_NetTCPSetting[{template}].Prr", false,
                        "RFC 6937 fast recovery algorithm for smoother loss recovery");
                }

                AddCimParam(list, "tcp.maxsynretx", "Max SYN Retransmissions", "Advanced",
                    Get(row, "MaxSynRetransmissions"), "4",
                    $"MSFT_NetTCPSetting[{template}].MaxSynRetransmissions", false,
                    "Retries before failing unreachable handshake (2 optimal)");

                AddCimParam(list, "tcp.nonsackrtt", "NonSack Rtt Resiliency", "Advanced",
                    DecodeEnabledDisabled(Get(row, "NonSackRttResiliency")), "Disabled",
                    $"MSFT_NetTCPSetting[{template}].NonSackRttResiliency", false,
                    "Keep Disabled on modern SACK-capable networks");

                AddCimParam(list, "tcp.initialrto", "Initial RTO (ms)", "Advanced",
                    Get(row, "InitialRtoMs"), "3000",
                    $"MSFT_NetTCPSetting[{template}].InitialRtoMs", false,
                    "Initial retransmission timeout (2000 optimal, 1000 gaming)");

                AddCimParam(list, "tcp.minrto", "Min RTO (ms)", "Advanced",
                    Get(row, "MinRtoMs"), "300",
                    $"MSFT_NetTCPSetting[{template}].MinRtoMs", false,
                    "Minimum retransmission floor (300ms safe baseline)");

                AddCimParam(list, "tcp.icw", "Initial Congestion Window (MSS)", "Advanced",
                    Get(row, "InitialCongestionWindowMss"), "10",
                    $"MSFT_NetTCPSetting[{template}].InitialCongestionWindowMss", false,
                    "Initial congestion window in MSS segments (10 RFC 6928 standard)");
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
            foreach (Dictionary<string, string> row in QueryCim("SELECT * FROM MSFT_NetOffloadGlobalSetting", cancellationToken))
            {
                AddCimParam(list, "offload.rss", "Receive-Side Scaling (RSS)", "General",
                    DecodeEnabledDisabled(Get(row, "ReceiveSideScaling")), "Enabled",
                    "MSFT_NetOffloadGlobalSetting.ReceiveSideScaling", false,
                    "Distributes packet processing across multiple CPU cores");

                AddCimParam(list, "offload.rsc", "Receive Segment Coalescing (RSC)", "General",
                    DecodeEnabledDisabled(Get(row, "ReceiveSegmentCoalescing")), "Enabled",
                    "MSFT_NetOffloadGlobalSetting.ReceiveSegmentCoalescing", false,
                    "Coalesces incoming TCP segments; disable in Gaming mode to lower jitter");
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
            foreach (Dictionary<string, string> row in QueryCim("SELECT Name, RegistryKeyword, DisplayName, DisplayValue, RegistryValue, ValidDisplayValues FROM MSFT_NetAdapterAdvancedPropertySettingData", cancellationToken))
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

                string validRaw = Get(row, "ValidDisplayValues");
                IReadOnlyList<string>? validVals = string.IsNullOrWhiteSpace(validRaw)
                    ? null
                    : validRaw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                list.Add(new AdapterAdvancedPropertyInfo(
                    RegistryKeyword: keyword,
                    DisplayName: Get(row, "DisplayName") is { Length: > 0 } dn ? dn : keyword,
                    DisplayValue: Get(row, "DisplayValue"),
                    RegistryValue: Get(row, "RegistryValue"),
                    ValidDisplayValues: validVals));
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

    public static string DecodeEnabledDisabled(string raw)
    {
        string t = raw.Trim();
        return t.ToLowerInvariant() switch
        {
            "0" or "disabled" or "disable" or "false" => "Disabled",
            "1" or "enabled" or "enable" or "true" => "Enabled",
            "2" or "default" => "Default",
            _ => t
        };
    }

    public static string DecodeDefaultEnabledDisabled(string raw) => DecodeEnabledDisabled(raw);

    public static string DecodeAutoTuning(string raw) => NormalizeAutoTuning(raw);

    public static string NormalizeAutoTuning(string raw)
    {
        string t = raw.Trim();
        return t.ToLowerInvariant() switch
        {
            "0" or "disabled" => "Disabled",
            "1" or "highlyrestricted" => "HighlyRestricted",
            "2" or "restricted" => "Restricted",
            "3" or "normal" => "Normal",
            "4" or "experimental" => "Experimental",
            _ => t
        };
    }

    public static string DecodeCongestion(string raw)
    {
        string t = raw.Trim();
        return t.ToLowerInvariant() switch
        {
            "0" or "default" => "Default",
            "1" or "none" => "None",
            "2" or "ctcp" => "CTCP",
            "4" or "dctcp" => "DCTCP",
            "5" or "cubic" => "CUBIC",
            "6" or "bbr2" => "BBR2",
            _ => t.ToUpperInvariant()
        };
    }

    public static string DecodePacingProfile(string raw)
    {
        string t = raw.Trim();
        return t.ToLowerInvariant() switch
        {
            "0" or "off" => "Off",
            "1" or "slowstart" => "SlowStart",
            "2" or "initialwindow" => "InitialWindow",
            "3" or "always" => "Always",
            _ => t
        };
    }

    private static void AddCimParam(
        List<NetworkParameterState> list,
        string id,
        string displayName,
        string category,
        string value,
        string defaultValue,
        string exactPath,
        bool requiresReboot,
        string? description = null)
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
            RequiresReboot: requiresReboot,
            Description: description));
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
            return string.Join("|", arr.Cast<object>().Select(x => x?.ToString() ?? string.Empty));
        }

        return val.ToString() ?? string.Empty;
    }
}
