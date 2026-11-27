using System.Globalization;
using System.IO;
using System.Text;
using FluentNet.Core.Models;
#if NET8_0_OR_GREATER
using System.Text.Json;
using FluentNet.Core.Serialization;
#endif

namespace FluentNet.Core.Services;

public sealed record SnapshotSummary(
    string FilePath,
    string FileName,
    string SnapshotId,
    string ProfileName,
    string OsBuild,
    DateTimeOffset CreatedAtUtc,
    int ChangeCount);

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

    public async Task<string> SaveSnapshotAsync(SnapshotManifest manifest, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(SnapshotDirectory);
        string filePath = Path.Combine(SnapshotDirectory, $"{manifest.SnapshotId}.json");
        string json = SerializeManifest(manifest);
#if NET8_0_OR_GREATER
        await File.WriteAllTextAsync(filePath, json, cancellationToken).ConfigureAwait(false);
#else
        await Task.Run(() => File.WriteAllText(filePath, json, Encoding.UTF8), cancellationToken).ConfigureAwait(false);
#endif
        return filePath;
    }

    public async Task<SnapshotManifest?> LoadSnapshotAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) return null;
#if NET8_0_OR_GREATER
        string json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, FluentNetJsonContext.Default.SnapshotManifest);
#else
        string json = await Task.Run(() => File.ReadAllText(filePath, Encoding.UTF8), cancellationToken).ConfigureAwait(false);
        return ParseManifestFallback(json);
#endif
    }

    public bool DeleteSnapshot(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        File.Delete(filePath);
        return true;
    }

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

    public async Task<IReadOnlyList<SnapshotSummary>> ListSnapshotSummariesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> files = ListSnapshotFiles();
        var list = new List<SnapshotSummary>(files.Count);
        foreach (string path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                SnapshotManifest? snap = await LoadSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                if (snap is not null)
                {
                    list.Add(new SnapshotSummary(
                        FilePath: path,
                        FileName: Path.GetFileName(path),
                        SnapshotId: snap.SnapshotId,
                        ProfileName: snap.ProfileName,
                        OsBuild: snap.OsBuild,
                        CreatedAtUtc: snap.CreatedAtUtc,
                        ChangeCount: snap.Items.Count));
                    continue;
                }
            }
            catch
            {
                // Fall back to filesystem metadata if file is malformed
            }

            list.Add(new SnapshotSummary(
                FilePath: path,
                FileName: Path.GetFileName(path),
                SnapshotId: Path.GetFileNameWithoutExtension(path),
                ProfileName: "Snapshot",
                OsBuild: "Unknown",
                CreatedAtUtc: new DateTimeOffset(File.GetCreationTimeUtc(path), TimeSpan.Zero),
                ChangeCount: 0));
        }

        return list;
    }

    public static string SerializeManifest(SnapshotManifest manifest)
    {
#if NET8_0_OR_GREATER
        return JsonSerializer.Serialize(manifest, FluentNetJsonContext.Default.SnapshotManifest);
#else
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"snapshotId\": \"{Esc(manifest.SnapshotId)}\",");
        sb.AppendLine($"  \"createdAtUtc\": \"{manifest.CreatedAtUtc:O}\",");
        sb.AppendLine($"  \"osBuild\": \"{Esc(manifest.OsBuild)}\",");
        sb.AppendLine($"  \"profileName\": \"{Esc(manifest.ProfileName)}\",");
        sb.AppendLine("  \"items\": [");
        for (int i = 0; i < manifest.Items.Count; i++)
        {
            ChangePlanItem it = manifest.Items[i];
            string comma = i < manifest.Items.Count - 1 ? "," : string.Empty;
            sb.AppendLine("    {");
            sb.AppendLine($"      \"parameterId\": \"{Esc(it.ParameterId)}\",");
            sb.AppendLine($"      \"displayName\": \"{Esc(it.DisplayName)}\",");
            sb.AppendLine($"      \"oldValue\": \"{Esc(it.OldValue)}\",");
            sb.AppendLine($"      \"newValue\": \"{Esc(it.NewValue)}\",");
            sb.AppendLine($"      \"windowsDefaultValue\": \"{Esc(it.WindowsDefaultValue)}\",");
            sb.AppendLine($"      \"exactPath\": \"{Esc(it.ExactPath)}\",");
            sb.AppendLine($"      \"sourceKind\": \"{it.SourceKind}\",");
            sb.AppendLine($"      \"requiresReboot\": {(it.RequiresReboot ? "true" : "false")}");
            sb.AppendLine($"    }}{comma}");
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
#endif
    }

#if !NET8_0_OR_GREATER
    private static string Esc(string? s)
        => (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

    private static SnapshotManifest? ParseManifestFallback(string json)
    {
        string id = ExtractStr(json, "snapshotId") ?? "unknown";
        string createdStr = ExtractStr(json, "createdAtUtc") ?? string.Empty;
        _ = DateTimeOffset.TryParse(createdStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset created);
        string osBuild = ExtractStr(json, "osBuild") ?? "Unknown";
        string profile = ExtractStr(json, "profileName") ?? "Unknown";

        var items = new List<ChangePlanItem>();
        int itemsIdx = json.IndexOf("\"items\"", StringComparison.OrdinalIgnoreCase);
        if (itemsIdx >= 0)
        {
            int pos = json.IndexOf('[', itemsIdx);
            while (pos >= 0 && pos < json.Length)
            {
                int objStart = json.IndexOf('{', pos);
                if (objStart < 0) break;
                int objEnd = json.IndexOf('}', objStart);
                if (objEnd < 0) break;
                string block = json.Substring(objStart, objEnd - objStart + 1);
                string? pid = ExtractStr(block, "parameterId");
                if (!string.IsNullOrWhiteSpace(pid))
                {
                    _ = Enum.TryParse(ExtractStr(block, "sourceKind") ?? "Registry", true, out ParameterSourceKind sk);
                    bool reboot = block.IndexOf("\"requiresReboot\": true", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  block.IndexOf("\"requiresReboot\":true", StringComparison.OrdinalIgnoreCase) >= 0;
                    items.Add(new ChangePlanItem(
                        pid!,
                        ExtractStr(block, "displayName") ?? pid!,
                        ExtractStr(block, "oldValue") ?? string.Empty,
                        ExtractStr(block, "newValue") ?? string.Empty,
                        ExtractStr(block, "windowsDefaultValue") ?? string.Empty,
                        ExtractStr(block, "exactPath") ?? string.Empty,
                        sk,
                        reboot));
                }
                pos = objEnd + 1;
            }
        }

        return new SnapshotManifest(id, created == default ? DateTimeOffset.UtcNow : created, osBuild, profile, items);
    }

    private static string? ExtractStr(string json, string key)
    {
        string token = $"\"{key}\"";
        int idx = json.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        int colon = json.IndexOf(':', idx + token.Length);
        if (colon < 0) return null;
        int q1 = json.IndexOf('"', colon + 1);
        if (q1 < 0) return null;
        var sb = new StringBuilder();
        for (int i = q1 + 1; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '\\' && i + 1 < json.Length)
            {
                char next = json[++i];
                sb.Append(next == 'n' ? '\n' : next == 'r' ? '\r' : next);
            }
            else if (c == '"')
            {
                return sb.ToString();
            }
            else
            {
                sb.Append(c);
            }
        }
        return null;
    }
#endif
}
