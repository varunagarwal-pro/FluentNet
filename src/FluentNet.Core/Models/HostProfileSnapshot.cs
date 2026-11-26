namespace FluentNet.Core.Models;

public enum AdapterMediaKind
{
    Ethernet,
    WiFi,
    VirtualOrTunnel,
    Other
}

public sealed record OsEnvironmentInfo(
    string ProductName,
    string DisplayVersion,
    int BuildNumber,
    int PhysicalCoreCount,
    int LogicalProcessorCount,
    bool IsWindows11,
    bool IsWin11_24H2OrNewer,
    bool SupportsBbr2,
    bool RequiresBbr2LoopbackGuard);

public sealed record AdapterAdvancedPropertyInfo(
    string RegistryKeyword,
    string DisplayName,
    string DisplayValue,
    string RegistryValue);

public sealed record NetworkAdapterInfo(
    string Alias,
    string Description,
    string InterfaceGuid,
    int InterfaceIndex,
    AdapterMediaKind MediaKind,
    long LinkSpeedMbps,
    int Ipv4Mtu,
    bool IsUp,
    bool IsPppoe,
    string? Ipv4Address,
    string? Ipv6Address,
    IReadOnlyList<AdapterAdvancedPropertyInfo> AdvancedProperties);

public sealed record HostScanReport(
    DateTimeOffset ScannedAtUtc,
    OsEnvironmentInfo OsInfo,
    IReadOnlyList<NetworkAdapterInfo> Adapters,
    IReadOnlyList<NetworkParameterState> Parameters);
