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

        var allParams = new List<NetworkParameterState>(cimTask.Result.Parameters.Count + regParams.Count);
        allParams.AddRange(cimTask.Result.Parameters);
        allParams.AddRange(regParams);

        return new HostScanReport(
            ScannedAtUtc: DateTimeOffset.UtcNow,
            OsInfo: osTask.Result,
            Adapters: adapters,
            Parameters: allParams);
    }
}
