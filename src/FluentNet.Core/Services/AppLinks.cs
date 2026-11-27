using System.Diagnostics;
using System.IO;

namespace FluentNet.Core.Services;

/// <summary>
/// Authoritative project, release, and developer URLs for FluentNet.
/// Compatible with both .NET 10 NativeAOT and .NET 4.8 Classic portable builds.
/// </summary>
public static class AppLinks
{
    public const string DeveloperName = "Varun Agarwal";
    public const string OrganizationName = "Sentinel Labs";
    public const string DeveloperHandle = "@varunagarwal-pro";
    public const string DeveloperProfile = "https://github.com/varunagarwal-pro";

    public const string GitHubRepo = "https://github.com/varunagarwal-pro/FluentNet";
    public const string ReleasesPage = "https://github.com/varunagarwal-pro/FluentNet/releases";
    public const string LatestReleasePage = "https://github.com/varunagarwal-pro/FluentNet/releases/latest";
    public const string IssuesPage = "https://github.com/varunagarwal-pro/FluentNet/issues";
    public const string LatestReleaseApi = "https://api.github.com/repos/varunagarwal-pro/FluentNet/releases/latest";
    public const string RawVersionUrl = "https://raw.githubusercontent.com/varunagarwal-pro/FluentNet/main/version.txt";

    public static bool OpenInBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
        return true;
    }

    public static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true
        });
    }
}
