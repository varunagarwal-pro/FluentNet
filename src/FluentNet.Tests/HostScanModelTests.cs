using System.Text.Json;
using FluentNet.Core.Models;
using FluentNet.Core.Serialization;
using Xunit;

namespace FluentNet.Tests;

public sealed class HostScanModelTests
{
    [Fact]
    public void HostScanReport_Serializes_WithWin11_24H2_GuardFlags()
    {
        var os = new OsEnvironmentInfo(
            ProductName: "Windows 11 Pro",
            DisplayVersion: "24H2",
            BuildNumber: 26100,
            PhysicalCoreCount: 8,
            LogicalProcessorCount: 16,
            IsWindows11: true,
            IsWin11_24H2OrNewer: true,
            SupportsBbr2: true,
            RequiresBbr2LoopbackGuard: true);

        var adapter = new NetworkAdapterInfo(
            Alias: "Ethernet",
            Description: "Intel(R) Ethernet Connection I219-LM",
            InterfaceGuid: "{E60A0550-39FB-46E8-BABE-90F3E0912538}",
            InterfaceIndex: 12,
            MediaKind: AdapterMediaKind.Ethernet,
            LinkSpeedMbps: 1000,
            Ipv4Mtu: 1500,
            IsUp: true,
            IsPppoe: false,
            Ipv4Address: "192.168.0.10",
            Ipv6Address: null,
            AdvancedProperties: Array.Empty<AdapterAdvancedPropertyInfo>());

        var report = new HostScanReport(
            ScannedAtUtc: new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero),
            OsInfo: os,
            Adapters: new[] { adapter },
            Parameters: Array.Empty<NetworkParameterState>());

        string json = JsonSerializer.Serialize(report, FluentNetJsonContext.Default.HostScanReport);
        HostScanReport? parsed = JsonSerializer.Deserialize(json, FluentNetJsonContext.Default.HostScanReport);

        Assert.NotNull(parsed);
        Assert.True(parsed.OsInfo.RequiresBbr2LoopbackGuard);
        Assert.Single(parsed.Adapters);
        Assert.Equal(AdapterMediaKind.Ethernet, parsed.Adapters[0].MediaKind);
    }
}
