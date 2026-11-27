using FluentNet.Core.Models;

namespace FluentNet.Core.Services;

public sealed class HostScanner
{
    private readonly HostDiscoveryService _discovery = new();
    private readonly CimTcpProvider _cim = new();
    private readonly Registry64Provider _registry = new();

    public async Task<HostScanReport> ScanAsync(CancellationToken cancellationToken = default)
    {
        Task<OsEnvironmentInfo> osTask = _discovery.ProbeOsAsync(cancellationToken);
        Task<(IReadOnlyList<NetworkParameterState> Parameters, IReadOnlyDictionary<string, IReadOnlyList<AdapterAdvancedPropertyInfo>> AdapterAdvanced)> cimTask = _cim.ReadAllAsync(cancellationToken);

        await Task.WhenAll(osTask, cimTask).ConfigureAwait(false);

        IReadOnlyList<NetworkAdapterInfo> adapters = await _discovery
            .DiscoverAdaptersAsync(cimTask.Result.AdapterAdvanced, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<NetworkParameterState> regParams = await _registry
            .ReadAllAsync(adapters, cancellationToken)
            .ConfigureAwait(false);

        var allParams = new List<NetworkParameterState>(cimTask.Result.Parameters.Count + regParams.Count + adapters.Count * 2);
        allParams.AddRange(cimTask.Result.Parameters);
        allParams.AddRange(regParams);

        // Expose per-adapter MTU and editable NIC Driver Advanced Properties in the unified parameter model
        foreach (NetworkAdapterInfo nic in adapters.Where(a => a.MediaKind is AdapterMediaKind.Ethernet or AdapterMediaKind.WiFi))
        {
            if (nic.InterfaceIndex > 0)
            {
                allParams.Add(new NetworkParameterState(
                    Id: $"nic.{nic.InterfaceGuid}.mtu",
                    DisplayName: $"[{nic.Alias}] IPv4 MTU",
                    Category: "General",
                    CurrentValue: nic.Ipv4Mtu.ToString(),
                    WindowsDefaultValue: nic.IsPppoe ? "1492" : "1500",
                    ExactPath: $"MSFT_NetIPInterface[ifIndex={nic.InterfaceIndex}].NlMtuBytes",
                    SourceKind: ParameterSourceKind.Cim,
                    SupportStatus: ParameterSupportStatus.Supported,
                    RequiresReboot: false,
                    Description: "Maximum Transmission Unit in bytes (1500 Ethernet/Wi-Fi, 1492 PPPoE)"));
            }

            foreach (AdapterAdvancedPropertyInfo prop in nic.AdvancedProperties)
            {
                allParams.Add(new NetworkParameterState(
                    Id: $"drv.{nic.InterfaceGuid}.{prop.RegistryKeyword}",
                    DisplayName: $"[{nic.Alias}] {prop.DisplayName}",
                    Category: "Driver",
                    CurrentValue: prop.DisplayValue,
                    WindowsDefaultValue: prop.DisplayValue,
                    ExactPath: $"MSFT_NetAdapterAdvancedPropertySettingData[Name='{nic.Alias}',RegistryKeyword='{prop.RegistryKeyword}']",
                    SourceKind: ParameterSourceKind.DriverAdvancedProperty,
                    SupportStatus: ParameterSupportStatus.Supported,
                    RequiresReboot: false,
                    Description: $"NIC driver property ({prop.RegistryKeyword})"));
            }
        }

        return new HostScanReport(
            ScannedAtUtc: DateTimeOffset.UtcNow,
            OsInfo: osTask.Result,
            Adapters: adapters,
            Parameters: allParams);
    }
}
