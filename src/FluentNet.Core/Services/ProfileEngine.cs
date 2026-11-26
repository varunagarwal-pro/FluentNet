using FluentNet.Core.Models;

namespace FluentNet.Core.Services;

public enum ProfileKind
{
    WindowsDefault,
    Current,
    RecommendedOptimal,
    GamingLowLatency,
    Custom
}

public sealed class ProfileEngine
{
    public IReadOnlyDictionary<string, string> BuildTargetValues(
        ProfileKind profile,
        HostScanReport report,
        NetworkAdapterInfo? selectedAdapter,
        bool preferBbr2 = false)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (profile == ProfileKind.Current)
        {
            foreach (NetworkParameterState p in report.Parameters)
            {
                map[p.Id] = p.CurrentValue;
            }
            return map;
        }

        if (profile == ProfileKind.WindowsDefault)
        {
            foreach (NetworkParameterState p in report.Parameters)
            {
                map[p.Id] = p.WindowsDefaultValue;
            }
            map["tcp.loopback_large_mtu"] = "enable";
            return map;
        }

        bool isGaming = profile == ProfileKind.GamingLowLatency;

        map["tcp.autotuning"] = "Normal";
        map["tcp.heuristics"] = "Disabled";
        map["tcp.ecn"] = isGaming ? "Disabled" : "Default";
        map["tcp.timestamps"] = "Disabled";
        map["tcp.maxsynretx"] = "2";
        map["tcp.nonsackrtt"] = "Disabled";
        map["tcp.initialrto"] = isGaming ? "1000" : "2000";
        map["tcp.minrto"] = "300";
        map["tcp.icw"] = "10";

        bool useBbr2 = preferBbr2 && report.OsInfo.SupportsBbr2;
        map["tcp.congestion"] = useBbr2 ? "BBR2" : "CUBIC";

        if (useBbr2 && report.OsInfo.RequiresBbr2LoopbackGuard)
        {
            // Prevent Win11 24H2/25H2 localhost/loopback stalls when BBR2 is active
            map["tcp.loopback_large_mtu"] = "disable";
        }
        else
        {
            map["tcp.loopback_large_mtu"] = "enable";
        }

        map["offload.rss"] = "Enabled";
        map["offload.rsc"] = isGaming ? "Disabled" : "Enabled";

        map["reg.default_ttl"] = "64";
        map["reg.max_user_port"] = "65534";
        map["reg.tcp_timed_wait_delay"] = "30";
        map["reg.network_throttling"] = "0xffffffff";
        map["reg.system_responsiveness"] = "10";
        map["reg.qos_non_best_effort"] = "0";
        map["reg.qos_do_not_use_nla"] = "1";
        map["reg.sp_local"] = "4";
        map["reg.sp_hosts"] = "5";
        map["reg.sp_dns"] = "6";
        map["reg.sp_netbt"] = "7";

        if (selectedAdapter is not null &&
            (selectedAdapter.MediaKind == AdapterMediaKind.Ethernet || selectedAdapter.MediaKind == AdapterMediaKind.WiFi))
        {
            string guid = selectedAdapter.InterfaceGuid;
            map[$"nic.{guid}.tcpackfreq"] = isGaming ? "1" : "2";
            map[$"nic.{guid}.tcpnodelay"] = isGaming ? "1" : "0";
            map[$"nic.{guid}.tcpdelackticks"] = isGaming ? "0" : "2";
        }

        return map;
    }
}
