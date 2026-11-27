using System.Xml.Linq;
using FluentNet.Core.Models;
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
    public void WinUiXaml_NeverUsesCornerRadius999Ovals()
    {
        string appDir = Path.Combine(RepoRoot, "src", "FluentNet.App");
        foreach (string xamlFile in Directory.GetFiles(appDir, "*.xaml", SearchOption.AllDirectories))
        {
            string content = File.ReadAllText(xamlFile);
            Assert.DoesNotContain("CornerRadius=\"999\"", content, StringComparison.Ordinal);
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
    public void ApplyEngine_DoesNotSpawnPowerShell()
    {
        string applyPath = Path.Combine(RepoRoot, "src", "FluentNet.Core", "Services", "ApplyEngine.cs");
        string source = File.ReadAllText(applyPath);
        Assert.DoesNotContain("powershell.exe", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ModifyInstance", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CimTcpProvider_DecodesNumericEnums_IntoReadableNames()
    {
        Assert.Equal("Disabled", CimTcpProvider.DecodeEnabledDisabled("0"));
        Assert.Equal("Enabled", CimTcpProvider.DecodeEnabledDisabled("1"));
        Assert.Equal("Normal", CimTcpProvider.DecodeAutoTuning("3"));
        Assert.Equal("CUBIC", CimTcpProvider.DecodeCongestion("5"));
        Assert.Equal("BBR2", CimTcpProvider.DecodeCongestion("6"));
        Assert.Equal("InitialWindow", CimTcpProvider.DecodePacingProfile("2"));
    }

    [Fact]
    public void HostDiscoveryService_FiltersFilterDriversAndDisconnectedVirtualAdapters()
    {
        Assert.True(HostDiscoveryService.IsNoiseName("Wi-Fi-Native WiFi Filter Driver-0000", "Intel(R) Wi-Fi 7 BE200-Native WiFi Filter Driver-0000"));
        Assert.True(HostDiscoveryService.IsNoiseName("Local Area Connection* 6", "WAN Miniport (IP)"));
        Assert.False(HostDiscoveryService.IsNoiseName("Wi-Fi", "Intel(R) Wi-Fi 7 BE200 320MHz"));

        var pangpDown = new NetworkAdapterInfo("Ethernet 2", "PANGP Virtual Ethernet Adapter Secure", "{1}", 2, AdapterMediaKind.VirtualOrTunnel, 2000, 1500, false, false, null, null, Array.Empty<AdapterAdvancedPropertyInfo>());
        var btDown = new NetworkAdapterInfo("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", "{2}", 3, AdapterMediaKind.VirtualOrTunnel, 3, 1500, false, false, "169.254.144.35", null, Array.Empty<AdapterAdvancedPropertyInfo>());
        var wifiUp = new NetworkAdapterInfo("Wi-Fi", "Intel(R) Wi-Fi 7 BE200 320MHz", "{3}", 4, AdapterMediaKind.WiFi, 702, 1500, true, false, "192.168.29.135", null, Array.Empty<AdapterAdvancedPropertyInfo>());

        Assert.True(HostDiscoveryService.IsGhostSecondaryAdapter(pangpDown));
        Assert.True(HostDiscoveryService.IsGhostSecondaryAdapter(btDown));
        Assert.False(HostDiscoveryService.IsGhostSecondaryAdapter(wifiUp));
    }

    [Fact]
    public void DnsOptimizerService_GradesLatencySweepsAccurately()
    {
        Assert.Equal("A+", DnsOptimizerService.GradeLatencySweep(new LatencySweepResult("1.1.1.1", 4, 4, 0, 4, 6, 8, 1.2)));
        Assert.Equal("A", DnsOptimizerService.GradeLatencySweep(new LatencySweepResult("8.8.8.8", 4, 4, 0, 28, 34, 41, 4.5)));
        Assert.Equal("F", DnsOptimizerService.GradeLatencySweep(new LatencySweepResult("dead", 4, 0, 100, 0, 0, 0, 0)));
    }
}
