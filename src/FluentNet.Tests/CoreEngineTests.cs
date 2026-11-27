using FluentNet.Core.Models;
using FluentNet.Core.Services;
using Xunit;

namespace FluentNet.Tests;

public sealed class CoreEngineTests
{
    [Fact]
    public void BdpCalculator_ComputesMssAlignedRwin_For100Mbps50ms()
    {
        BdpResult res = BdpCalculator.Calculate(100, BandwidthUnit.Mbps, 50, mtu: 1500);

        Assert.Equal(625_000, res.BdpBytes);
        Assert.Equal(1460, res.Mss);
        Assert.True(res.SuggestedRwinBytes >= res.BdpBytes);
        Assert.Equal(0, res.SuggestedRwinBytes % 1460);
        Assert.True(res.RecommendedScaleFactor >= 4);
    }

    [Fact]
    public void ProfileEngine_EnablesWin11_24H2_LoopbackGuard_WhenBbr2Selected()
    {
        var os = new OsEnvironmentInfo("Windows 11 Pro", "24H2", 26100, 8, 16, true, true, true, true);
        var nic = new NetworkAdapterInfo("Ethernet", "Intel I219", "{11111111-1111-1111-1111-111111111111}", 1, AdapterMediaKind.Ethernet, 1000, 1500, true, false, "192.168.0.10", null, Array.Empty<AdapterAdvancedPropertyInfo>());
        var report = new HostScanReport(DateTimeOffset.UtcNow, os, new[] { nic }, Array.Empty<NetworkParameterState>());

        var engine = new ProfileEngine();
        IReadOnlyDictionary<string, string> proposed = engine.BuildTargetValues(ProfileKind.RecommendedOptimal, report, nic, preferBbr2: true);

        Assert.Equal("BBR2", proposed["tcp.congestion"]);
        Assert.Equal("Disabled", proposed["tcp.loopback_large_mtu"]);
    }

    [Fact]
    public void DiffEngine_And_ScriptExporter_EmitAuditableDiffsAndReg()
    {
        var os = new OsEnvironmentInfo("Windows 11 Pro", "24H2", 26100, 8, 16, true, true, true, true);
        var current = new NetworkParameterState("reg.tcp_timed_wait_delay", "TcpTimedWaitDelay (s)", "Advanced", "120", "120", @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\TcpTimedWaitDelay", ParameterSourceKind.Registry, ParameterSupportStatus.Supported, true);
        var report = new HostScanReport(DateTimeOffset.UtcNow, os, Array.Empty<NetworkAdapterInfo>(), new[] { current });

        var engine = new ProfileEngine();
        IReadOnlyDictionary<string, string> proposed = engine.BuildTargetValues(ProfileKind.RecommendedOptimal, report, null, preferBbr2: true);

        var diffEngine = new DiffEngine();
        IReadOnlyList<ChangePlanItem> diffs = diffEngine.ComputeDiff(report, proposed);

        Assert.Contains(diffs, d => d.ParameterId == "reg.tcp_timed_wait_delay" && d.NewValue == "30");
        Assert.Contains(diffs, d => d.ParameterId == "tcp.loopback_large_mtu" && d.NewValue == "Disabled");

        string ps1 = ScriptExporter.ExportPowerShellApplyScript(diffs, "RecommendedOptimal");
        string reg = ScriptExporter.ExportRegistryFile(diffs);

        Assert.Contains("Set-ItemProperty", ps1);
        Assert.Contains("\"TcpTimedWaitDelay\"=dword:0000001e", reg);
    }

    [Fact]
    public void DiffEngine_TreatsNotSetRegistryAndHexDecEquivalents_AsAligned()
    {
        var os = new OsEnvironmentInfo("Windows 11 Pro", "26H2", 26300, 14, 20, true, true, true, true);
        var nagleDefault = new NetworkParameterState("nic.{AAA}.tcpackfreq", "TcpAckFrequency", "Gaming", "NotSet", "2", @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{AAA}\TcpAckFrequency", ParameterSourceKind.Registry, ParameterSupportStatus.NotSet, true);
        var throttleHex = new NetworkParameterState("reg.network_throttling", "NetworkThrottlingIndex", "Gaming", "0x0000000a", "0x0000000a", @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\NetworkThrottlingIndex", ParameterSourceKind.Registry, ParameterSupportStatus.Supported, true);
        var ecnDisabled = new NetworkParameterState("tcp.ecn", "ECN Capability", "General", "Disabled", "Disabled", "MSFT_NetTCPSetting[Internet].EcnCapability", ParameterSourceKind.Cim, ParameterSupportStatus.Supported, false);

        var report = new HostScanReport(DateTimeOffset.UtcNow, os, Array.Empty<NetworkAdapterInfo>(), new[] { nagleDefault, throttleHex, ecnDisabled });

        var proposed = new Dictionary<string, string>
        {
            ["nic.{AAA}.tcpackfreq"] = "2",
            ["reg.network_throttling"] = "10",
            ["tcp.ecn"] = "Default"
        };

        var diffEngine = new DiffEngine();
        IReadOnlyList<ChangePlanItem> diffs = diffEngine.ComputeDiff(report, proposed);

        Assert.Empty(diffs);
    }

    [Fact]
    public void ScriptExporter_EmitsRemoveSyntax_ForNotSetRegistryValues()
    {
        var item = new ChangePlanItem(
            "reg.max_user_port",
            "MaxUserPort",
            "65534",
            "NotSet",
            "NotSet",
            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\MaxUserPort",
            ParameterSourceKind.Registry,
            true);

        string ps1 = ScriptExporter.ExportPowerShellApplyScript(new[] { item }, "WindowsDefault");
        string reg = ScriptExporter.ExportRegistryFile(new[] { item });

        Assert.Contains("Remove-ItemProperty", ps1);
        Assert.Contains("\"MaxUserPort\"=-", reg);
    }
}
