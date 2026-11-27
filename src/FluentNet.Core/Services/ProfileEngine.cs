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
                map[p.Id] = p.SourceKind == ParameterSourceKind.Registry
                    ? "NotSet"
                    : p.WindowsDefaultValue;
            }
            map["tcp.loopback_large_mtu"] = "Enabled";
            return map;
        }

        bool isGaming = profile == ProfileKind.GamingLowLatency;

        map["tcp.autotuning"] = "Normal";
        map["tcp.heuristics"] = "Disabled";
        map["tcp.ecn"] = "Disabled";
        map["tcp.timestamps"] = "Disabled";
        map["tcp.fastopen"] = "Enabled";
        map["tcp.hystart"] = "Enabled";
        map["tcp.prr"] = "Enabled";
        map["tcp.maxsynretx"] = "2";
        map["tcp.nonsackrtt"] = "Disabled";
        map["tcp.initialrto"] = isGaming ? "1000" : "2000";
        map["tcp.minrto"] = "300";

        bool useBbr2 = preferBbr2 && report.OsInfo.SupportsBbr2;
        map["tcp.congestion"] = useBbr2 ? "BBR2" : "CUBIC";

        map["tcp.loopback_large_mtu"] = (useBbr2 && report.OsInfo.RequiresBbr2LoopbackGuard)
            ? "Disabled"
            : "Enabled";

        map["offload.rss"] = "Enabled";
        map["offload.rsc"] = isGaming ? "Disabled" : "Enabled";

        map["reg.default_ttl"] = "64";
        map["reg.max_user_port"] = "65534";
        map["reg.tcp_timed_wait_delay"] = "30";
        map["reg.afd_fast_send"] = "1500";
        map["reg.network_throttling"] = "0xffffffff";
        map["reg.system_responsiveness"] = isGaming ? "0" : "10";
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
            if (isGaming)
            {
                map[$"nic.{guid}.tcpackfreq"] = "1";
                map[$"nic.{guid}.tcpnodelay"] = "1";
                map[$"nic.{guid}.tcpdelackticks"] = "0";
            }
            else
            {
                map[$"nic.{guid}.tcpackfreq"] = "NotSet";
                map[$"nic.{guid}.tcpnodelay"] = "NotSet";
                map[$"nic.{guid}.tcpdelackticks"] = "NotSet";
            }
        }

        return map;
    }

    /// <summary>
    /// Computes speed-tier-aware Windows 10/11 tuning overrides when the user drags the Connection Speed slider.
    /// Grounded in real modern Windows TCP/IP parameters (no legacy WinXP static RWIN snake-oil).
    /// </summary>
    public IReadOnlyDictionary<string, string> BuildSpeedTierOverrides(int speedMbps)
    {
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tcp.autotuning"] = "Normal",
            ["tcp.heuristics"] = "Disabled",
            ["offload.rss"] = "Enabled",
            ["reg.default_ttl"] = "64",
            ["reg.qos_non_best_effort"] = "0"
        };

        if (speedMbps < 50)
        {
            // Low-bandwidth / DSL / Cellular: conservative RTO & retry budget, pacing off
            overrides["tcp.initialrto"] = "3000";
            overrides["tcp.maxsynretx"] = "3";
            overrides["tcp.pacing"] = "Off";
            overrides["offload.rsc"] = "Enabled";
            overrides["reg.afd_fast_send"] = "1024";
            overrides["reg.tcp_timed_wait_delay"] = "60";
            overrides["reg.network_throttling"] = "0x0000000a";
        }
        else if (speedMbps < 300)
        {
            // Broadband / Cable / Mid-tier Fiber
            overrides["tcp.initialrto"] = "2000";
            overrides["tcp.maxsynretx"] = "2";
            overrides["tcp.pacing"] = "Off";
            overrides["offload.rsc"] = "Enabled";
            overrides["reg.afd_fast_send"] = "1500";
            overrides["reg.max_user_port"] = "65534";
            overrides["reg.tcp_timed_wait_delay"] = "30";
            overrides["reg.network_throttling"] = "0xffffffff";
        }
        else
        {
            // Gigabit / Multi-Gig Fiber / Wi-Fi 6E & Wi-Fi 7
            overrides["tcp.initialrto"] = "1000";
            overrides["tcp.maxsynretx"] = "2";
            overrides["tcp.pacing"] = "InitialWindow";
            overrides["offload.rsc"] = "Enabled";
            overrides["reg.afd_fast_send"] = "1500";
            overrides["reg.max_user_port"] = "65534";
            overrides["reg.tcp_timed_wait_delay"] = "30";
            overrides["reg.network_throttling"] = "0xffffffff";
            overrides["reg.system_responsiveness"] = "10";
        }

        return overrides;
    }
}
