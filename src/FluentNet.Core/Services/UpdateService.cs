using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FluentNet.Core.Services;

public enum UpdateEdition
{
    WinUI3,
    ClassicWpf
}

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    NoPublishedRelease,
    Error
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string CurrentVersion,
    string LatestVersion,
    string ReleasePageUrl,
    string? AssetDownloadUrl,
    string? ChecksumsUrl,
    string? ReleaseNotes,
    string StatusMessage);

public sealed record UpdateDownloadResult(
    bool Succeeded,
    string? LocalFilePath,
    bool ChecksumVerified,
    string Message);

/// <summary>
/// Dual-runtime (.NET 10 NativeAOT + .NET 4.8 WPF) GitHub Release & version.txt update checker with SHA-256 package staging.
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient SharedHttpClient = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FluentNet-Updater/1.0 (+https://github.com/varunagarwal-pro/FluentNet)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static string GetInstalledVersion(Assembly? assembly = null)
    {
        Version? v = (assembly ?? Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version;
        return v is null ? "0.1.5" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        string currentVersion,
        UpdateEdition edition,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage releaseResp = await SharedHttpClient.GetAsync(AppLinks.LatestReleaseApi, cancellationToken).ConfigureAwait(false);
            if (releaseResp.IsSuccessStatusCode)
            {
                string json = await releaseResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseGitHubReleaseJson(currentVersion, json, edition);
            }

            // If no GitHub Release exists yet or rate limited, fall back to raw version.txt like FluentCleaner.
            if (releaseResp.StatusCode == HttpStatusCode.NotFound || (int)releaseResp.StatusCode == 403)
            {
                using HttpResponseMessage rawResp = await SharedHttpClient.GetAsync(AppLinks.RawVersionUrl, cancellationToken).ConfigureAwait(false);
                if (rawResp.IsSuccessStatusCode)
                {
                    string rawText = await rawResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return ParseRawVersionText(currentVersion, rawText);
                }

                if (releaseResp.StatusCode == HttpStatusCode.NotFound && rawResp.StatusCode == HttpStatusCode.NotFound)
                {
                    return new UpdateCheckResult(
                        UpdateCheckStatus.NoPublishedRelease,
                        NormalizeVersion(currentVersion),
                        NormalizeVersion(currentVersion),
                        AppLinks.GitHubRepo,
                        null,
                        null,
                        null,
                        $"No public GitHub release published yet. Installed v{NormalizeVersion(currentVersion)}.");
                }
            }

            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                NormalizeVersion(currentVersion),
                NormalizeVersion(currentVersion),
                AppLinks.ReleasesPage,
                null,
                null,
                null,
                $"GitHub update server returned HTTP {(int)releaseResp.StatusCode} ({releaseResp.ReasonPhrase}).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                NormalizeVersion(currentVersion),
                NormalizeVersion(currentVersion),
                AppLinks.ReleasesPage,
                null,
                null,
                null,
                $"Unable to reach GitHub update service: {ex.Message}");
        }
    }

    public async Task<UpdateDownloadResult> DownloadReleasePackageAsync(
        UpdateCheckResult check,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(check.AssetDownloadUrl))
        {
            return new UpdateDownloadResult(false, null, false, "No direct zip package URL is available for this release.");
        }

        try
        {
            string updatesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FluentNet",
                "Updates");
            Directory.CreateDirectory(updatesDir);

            string fileName = Path.GetFileName(new Uri(check.AssetDownloadUrl).LocalPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = $"FluentNet-v{check.LatestVersion}.zip";
            }

            string targetPath = Path.Combine(updatesDir, fileName);
            using HttpResponseMessage pkgResp = await SharedHttpClient.GetAsync(check.AssetDownloadUrl, cancellationToken).ConfigureAwait(false);
            pkgResp.EnsureSuccessStatusCode();
            byte[] payload = await pkgResp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

            bool checksumVerified = false;
            if (!string.IsNullOrWhiteSpace(check.ChecksumsUrl))
            {
                using HttpResponseMessage sumResp = await SharedHttpClient.GetAsync(check.ChecksumsUrl, cancellationToken).ConfigureAwait(false);
                if (sumResp.IsSuccessStatusCode)
                {
                    string sumsText = await sumResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    string actualSha = ComputeSha256Hex(payload);
                    if (sumsText.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (sumsText.IndexOf(actualSha, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            return new UpdateDownloadResult(false, null, false, $"SHA-256 verification failed for {fileName}. Download aborted for safety.");
                        }
                        checksumVerified = true;
                    }
                }
            }

            await Task.Run(() => File.WriteAllBytes(targetPath, payload), cancellationToken).ConfigureAwait(false);
            string msg = checksumVerified
                ? $"Downloaded and SHA-256 verified: {targetPath}"
                : $"Downloaded package to {targetPath}";
            return new UpdateDownloadResult(true, targetPath, checksumVerified, msg);
        }
        catch (Exception ex)
        {
            return new UpdateDownloadResult(false, null, false, $"Download failed: {ex.Message}");
        }
    }

    public static UpdateCheckResult ParseRawVersionText(string currentVersion, string rawContent)
    {
        string local = NormalizeVersion(currentVersion);
        string remote = NormalizeVersion(rawContent);
        if (string.IsNullOrWhiteSpace(remote))
        {
            return new UpdateCheckResult(UpdateCheckStatus.Error, local, local, AppLinks.ReleasesPage, null, null, null, "Remote version.txt was empty.");
        }

        bool hasUpdate = CompareVersions(remote, local) > 0;
        return new UpdateCheckResult(
            hasUpdate ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate,
            local,
            remote,
            AppLinks.ReleasesPage,
            null,
            null,
            null,
            hasUpdate ? $"FluentNet v{remote} is available (installed v{local})." : $"You are up to date on v{local}.");
    }

    public static UpdateCheckResult ParseGitHubReleaseJson(string currentVersion, string json, UpdateEdition edition)
    {
        string local = NormalizeVersion(currentVersion);
        string tag = ExtractJsonStringValue(json, "tag_name") ?? string.Empty;
        string remote = NormalizeVersion(tag);
        if (string.IsNullOrWhiteSpace(remote))
        {
            return new UpdateCheckResult(UpdateCheckStatus.Error, local, local, AppLinks.ReleasesPage, null, null, null, "Release metadata did not include tag_name.");
        }

        string htmlUrl = ExtractJsonStringValue(json, "html_url") ?? AppLinks.LatestReleasePage;
        string body = (ExtractJsonStringValue(json, "body") ?? string.Empty).Trim();

        List<(string Name, string Url)> assets = ExtractReleaseAssets(json);
        string archToken = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

        string? assetUrl = null;
        string? checksumsUrl = null;
        foreach ((string name, string url) in assets)
        {
            if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                checksumsUrl = url;
                continue;
            }

            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool isClassicAsset = name.IndexOf("Classic", StringComparison.OrdinalIgnoreCase) >= 0;
            bool matchesEdition = edition == UpdateEdition.ClassicWpf ? isClassicAsset : !isClassicAsset;
            if (matchesEdition && name.IndexOf(archToken, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                assetUrl = url;
            }
        }

        bool hasUpdate = CompareVersions(remote, local) > 0;
        string message = hasUpdate
            ? $"FluentNet v{remote} is available (you have v{local})."
            : $"You are running the latest version (v{local}).";

        return new UpdateCheckResult(
            hasUpdate ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate,
            local,
            remote,
            htmlUrl,
            assetUrl,
            checksumsUrl,
            body,
            message);
    }

    public static int CompareVersions(string left, string right)
    {
        int[] a = ParseSemVerParts(left);
        int[] b = ParseSemVerParts(right);
        for (int i = 0; i < 3; i++)
        {
            int cmp = a[i].CompareTo(b[i]);
            if (cmp != 0)
            {
                return cmp;
            }
        }
        return 0;
    }

    public static string NormalizeVersion(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "0.0.0";
        }

        string trimmed = raw.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring(1);
        }

        int dash = trimmed.IndexOf('-');
        if (dash >= 0)
        {
            trimmed = trimmed.Substring(0, dash);
        }

        int[] parts = ParseSemVerParts(trimmed);
        return $"{parts[0]}.{parts[1]}.{parts[2]}";
    }

    private static int[] ParseSemVerParts(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new[] { 0, 0, 0 };
        }

        string clean = raw.Trim();
        if (clean.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring(1);
        }

        int dash = clean.IndexOf('-');
        if (dash >= 0)
        {
            clean = clean.Substring(0, dash);
        }

        string[] segs = clean.Split('.');
        int major = segs.Length > 0 && int.TryParse(segs[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) ? m : 0;
        int minor = segs.Length > 1 && int.TryParse(segs[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
        int patch = segs.Length > 2 && int.TryParse(segs[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) ? p : 0;
        return new[] { major, minor, patch };
    }

    private static string ComputeSha256Hex(byte[] data)
    {
        using SHA256 sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(data);
        return BitConverter.ToString(digest).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static List<(string Name, string Url)> ExtractReleaseAssets(string json)
    {
        var results = new List<(string, string)>();
        int idx = json.IndexOf("\"assets\"", StringComparison.Ordinal);
        if (idx < 0)
        {
            return results;
        }

        int scan = idx;
        while (scan < json.Length)
        {
            int urlPos = json.IndexOf("\"browser_download_url\"", scan, StringComparison.Ordinal);
            if (urlPos < 0)
            {
                break;
            }

            string? url = ExtractJsonStringAtKey(json, urlPos + "\"browser_download_url\"".Length);
            if (!string.IsNullOrWhiteSpace(url))
            {
                string fileName = Path.GetFileName(new Uri(url, UriKind.Absolute).LocalPath);
                results.Add((fileName, url!));
            }

            scan = urlPos + 22;
        }

        return results;
    }

    private static string? ExtractJsonStringValue(string json, string key)
    {
        string token = $"\"{key}\"";
        int idx = 0;
        while (idx < json.Length)
        {
            int found = json.IndexOf(token, idx, StringComparison.Ordinal);
            if (found < 0)
            {
                return null;
            }

            string? val = ExtractJsonStringAtKey(json, found + token.Length);
            if (val is not null)
            {
                return val;
            }

            idx = found + token.Length;
        }
        return null;
    }

    private static string? ExtractJsonStringAtKey(string json, int afterKeyIdx)
    {
        int i = afterKeyIdx;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length || json[i] != ':') return null;
        i++;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length || json[i] != '"') return null;
        i++;

        var sb = new StringBuilder();
        while (i < json.Length)
        {
            char c = json[i++];
            if (c == '"')
            {
                return sb.ToString();
            }

            if (c == '\\' && i < json.Length)
            {
                char esc = json[i++];
                switch (esc)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    default: sb.Append(esc); break;
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        return null;
    }
}
