using System.Xml.Linq;
using FluentNet.Core.Services;
using Xunit;

namespace FluentNet.Tests;

public sealed class UiAndEngineRegressionTests
{
    private static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void MainWindowXaml_ScrollViewers_ReserveRightSpace_And_KeepHeaderActionsFixed()
    {
        string xamlPath = Path.Combine(RepoRoot, "src", "FluentNet.App", "MainWindow.xaml");
        XDocument doc = XDocument.Load(xamlPath);
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        List<XElement> scrollViewers = doc.Descendants(ns + "ScrollViewer").ToList();
        Assert.NotEmpty(scrollViewers);

        foreach (XElement sv in scrollViewers)
        {
            XElement? content = sv.Elements().FirstOrDefault();
            Assert.NotNull(content);

            string? margin = content!.Attribute("Margin")?.Value ?? content.Attribute("Padding")?.Value;
            Assert.False(string.IsNullOrWhiteSpace(margin), $"ScrollViewer '{sv.Attribute("Name")?.Value}' child must reserve right margin/padding >= 18px for scrollbar clearance.");

            string[] parts = margin!.Split(',');
            Assert.True(parts.Length == 4 && double.TryParse(parts[2], out double right) && right >= 18,
                $"ScrollViewer '{sv.Attribute("Name")?.Value}' right margin/padding '{margin}' must be >= 18px so scrollbar never overlaps UI controls.");
        }
    }

    [Fact]
    public void DiffDialog_UsesDedicatedWideReviewDialog_WithOverriddenMaxWidth()
    {
        string appDir = Path.Combine(RepoRoot, "src", "FluentNet.App");
        string mainCs = File.ReadAllText(Path.Combine(appDir, "MainWindow.xaml.cs"));
        string dialogXamlPath = Path.Combine(appDir, "ReviewChangesDialog.xaml");

        Assert.True(File.Exists(dialogXamlPath), "A dedicated ReviewChangesDialog.xaml is required instead of an inline StackPanel ContentDialog.");
        string dialogXaml = File.ReadAllText(dialogXamlPath);
        Assert.Contains("ContentDialogMaxWidth", dialogXaml);
        Assert.Contains("ReviewChangesDialog", mainCs);
    }

    [Fact]
    public void CimTcpProvider_DecodesNumericEnums_IntoReadableNames()
    {
        Assert.Equal("Disabled", CimTcpProvider.DecodeEnabledDisabled("0"));
        Assert.Equal("Enabled", CimTcpProvider.DecodeEnabledDisabled("1"));
        Assert.Equal("Normal", CimTcpProvider.DecodeAutoTuning("3"));
        Assert.Equal("CUBIC", CimTcpProvider.DecodeCongestion("5"));
        Assert.Equal("BBR2", CimTcpProvider.DecodeCongestion("6"));
    }

    [Fact]
    public void HostDiscoveryService_FiltersFilterDriversAndMiniports()
    {
        Assert.True(HostDiscoveryService.IsNoiseName("Wi-Fi-Native WiFi Filter Driver-0000", "Intel(R) Wi-Fi 7 BE200-Native WiFi Filter Driver-0000"));
        Assert.True(HostDiscoveryService.IsNoiseName("Local Area Connection* 6", "WAN Miniport (IP)"));
        Assert.False(HostDiscoveryService.IsNoiseName("Wi-Fi", "Intel(R) Wi-Fi 7 BE200 320MHz"));
        Assert.False(HostDiscoveryService.IsNoiseName("Ethernet", "Intel(R) Ethernet Connection (17) I219-LM"));
    }
}

