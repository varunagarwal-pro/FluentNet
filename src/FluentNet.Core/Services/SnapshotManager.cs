using System.IO;
using FluentNet.Core.Models;
#if NET8_0_OR_GREATER
using System.Text.Json;
using FluentNet.Core.Serialization;
#endif

namespace FluentNet.Core.Services;

public sealed class SnapshotManager
{
    public string SnapshotDirectory { get; }

    public SnapshotManager(string? customDirectory = null)
    {
        SnapshotDirectory = customDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluentNet",
            "Snapshots");
    }

#if NET8_0_OR_GREATER
    public async Task<string> SaveSnapshotAsync(SnapshotManifest manifest, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(SnapshotDirectory);
        string filePath = Path.Combine(SnapshotDirectory, $"{manifest.SnapshotId}.json");
        string json = JsonSerializer.Serialize(manifest, FluentNetJsonContext.Default.SnapshotManifest);
        await File.WriteAllTextAsync(filePath, json, cancellationToken).ConfigureAwait(false);
        return filePath;
    }

    public async Task<SnapshotManifest?> LoadSnapshotAsync(string filePath, CancellationToken cancellationToken = default)
    {
        string json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, FluentNetJsonContext.Default.SnapshotManifest);
    }
#endif

    public IReadOnlyList<string> ListSnapshotFiles()
    {
        if (!Directory.Exists(SnapshotDirectory))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(SnapshotDirectory, "*.json")
            .OrderByDescending(File.GetCreationTimeUtc)
            .ToList();
    }
}
