using FluentNet.Core.Services;
using Xunit;

namespace FluentNet.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("0.1.4", "0.1.5", 1)]
    [InlineData("0.1.4", "v0.1.5", 1)]
    [InlineData("v0.1.4", "v0.2.0", 1)]
    [InlineData("0.1.5", "0.1.5", 0)]
    [InlineData("0.2.0", "0.1.9", -1)]
    [InlineData("0.1.4", "v0.1.5-rc.1", 1)]
    public void CompareVersions_HandlesSemVerAndTagPrefixes(string current, string remote, int expectedSign)
    {
        int actual = UpdateService.CompareVersions(remote, current);
        Assert.Equal(expectedSign, Math.Sign(actual));
    }

    [Fact]
    public void ParseGitHubReleaseJson_ExtractsTagNotesAndMatchingAsset()
    {
        const string sampleJson = """
        {
          "tag_name": "v0.2.0",
          "html_url": "https://github.com/varunagarwal-pro/FluentNet/releases/tag/v0.2.0",
          "body": "## What's New\r\n- Faster BDP sweep\n- Fixed ScrollViewer",
          "assets": [
            {
              "name": "FluentNet-v0.2.0-win-arm64.zip",
              "browser_download_url": "https://github.com/varunagarwal-pro/FluentNet/releases/download/v0.2.0/FluentNet-v0.2.0-win-arm64.zip"
            },
            {
              "name": "FluentNet-v0.2.0-win-x64.zip",
              "browser_download_url": "https://github.com/varunagarwal-pro/FluentNet/releases/download/v0.2.0/FluentNet-v0.2.0-win-x64.zip"
            },
            {
              "name": "FluentNet-Classic-v0.2.0-win-x64.zip",
              "browser_download_url": "https://github.com/varunagarwal-pro/FluentNet/releases/download/v0.2.0/FluentNet-Classic-v0.2.0-win-x64.zip"
            },
            {
              "name": "SHA256SUMS.txt",
              "browser_download_url": "https://github.com/varunagarwal-pro/FluentNet/releases/download/v0.2.0/SHA256SUMS.txt"
            }
          ]
        }
        """;

        UpdateCheckResult modern = UpdateService.ParseGitHubReleaseJson("0.1.4", sampleJson, UpdateEdition.WinUI3);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, modern.Status);
        Assert.Equal("0.2.0", modern.LatestVersion);
        Assert.Equal("https://github.com/varunagarwal-pro/FluentNet/releases/download/v0.2.0/FluentNet-v0.2.0-win-x64.zip", modern.AssetDownloadUrl);
        Assert.Equal("https://github.com/varunagarwal-pro/FluentNet/releases/download/v0.2.0/SHA256SUMS.txt", modern.ChecksumsUrl);
        Assert.Contains("Faster BDP sweep", modern.ReleaseNotes);

        UpdateCheckResult classic = UpdateService.ParseGitHubReleaseJson("0.1.4", sampleJson, UpdateEdition.ClassicWpf);
        Assert.Equal("https://github.com/varunagarwal-pro/FluentNet/releases/download/v0.2.0/FluentNet-Classic-v0.2.0-win-x64.zip", classic.AssetDownloadUrl);
    }

    [Fact]
    public void ParseRawVersionText_DetectsUpdateOrUpToDate()
    {
        UpdateCheckResult newer = UpdateService.ParseRawVersionText("0.1.4", "0.1.6\n");
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, newer.Status);
        Assert.Equal("0.1.6", newer.LatestVersion);
        Assert.Equal(AppLinks.ReleasesPage, newer.ReleasePageUrl);

        UpdateCheckResult same = UpdateService.ParseRawVersionText("0.1.5", "0.1.5");
        Assert.Equal(UpdateCheckStatus.UpToDate, same.Status);
    }

    [Fact]
    public void AppLinks_TargetsVarunAgarwalRepository()
    {
        Assert.Equal("https://github.com/varunagarwal-pro/FluentNet", AppLinks.GitHubRepo);
        Assert.Equal("https://github.com/varunagarwal-pro", AppLinks.DeveloperProfile);
        Assert.StartsWith("https://api.github.com/repos/varunagarwal-pro/FluentNet/", AppLinks.LatestReleaseApi);
    }
}
