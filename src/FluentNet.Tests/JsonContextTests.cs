using System.Text.Json;
using FluentNet.Core.Models;
using FluentNet.Core.Serialization;
using Xunit;

namespace FluentNet.Tests;

public sealed class JsonContextTests
{
    [Fact]
    public void SnapshotManifest_RoundTrips_WithoutReflection()
    {
        var manifest = new SnapshotManifest(
            SnapshotId: "snap-20261127-001",
            CreatedAtUtc: new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero),
            OsBuild: "26100",
            ProfileName: "Optimal",
            Items: new[]
            {
                new ChangePlanItem(
                    ParameterId: "tcp.congestion",
                    DisplayName: "Congestion Control Provider",
                    OldValue: "CUBIC",
                    NewValue: "BBR2",
                    WindowsDefaultValue: "CUBIC",
                    ExactPath: "netsh int tcp set supplemental template=Internet congestionprovider=BBR2",
                    SourceKind: ParameterSourceKind.Netsh,
                    RequiresReboot: false)
            });

        string json = JsonSerializer.Serialize(manifest, FluentNetJsonContext.Default.SnapshotManifest);
        SnapshotManifest? roundTripped = JsonSerializer.Deserialize(json, FluentNetJsonContext.Default.SnapshotManifest);

        Assert.NotNull(roundTripped);
        Assert.Equal(manifest.SnapshotId, roundTripped.SnapshotId);
        Assert.Single(roundTripped.Items);
        Assert.Equal("BBR2", roundTripped.Items[0].NewValue);
    }
}
