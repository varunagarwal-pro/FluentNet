using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FluentNet.Core.Models;
using FluentNet.Core.Services;

namespace FluentNet.Classic;

public partial class MainWindow : Window
{
    private readonly HostScanner _scanner = new();
    private readonly ProfileEngine _profileEngine = new();
    private readonly DiffEngine _diffEngine = new();
    private readonly ApplyEngine _applyEngine = new();
    private readonly SnapshotManager _snapshotManager = new();
    private readonly IcmpDiagnosticsService _icmp = new();
    private readonly UpdateService _updater = new();
    private readonly string _appVersion;

    private HostScanReport? _lastReport;
    private Dictionary<string, string> _customOverrides = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _diagCts;
    private bool _syncing;

    public MainWindow()
    {
        InitializeComponent();
        _appVersion = UpdateService.GetInstalledVersion(typeof(MainWindow).Assembly);
        ClassicVersionText.Text = $"v{_appVersion} • MIT Open Source";
        string mode = ElevationLauncher.IsProcessElevated() ? "Administrator" : "Standard";
        Title = $"FluentNet Classic (x64 Portable • {mode}) v{_appVersion} — by {AppLinks.DeveloperName}";
        Loaded += async (_, _) => await RunScanAsync();
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        DashboardPanel.Visibility = tag == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        GeneralPanel.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        DiffGrid.Visibility = tag == "advanced" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPanel.Visibility = tag == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        SnapshotsPanel.Visibility = tag == "snapshots" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "snapshots") SnapshotsList.ItemsSource = _snapshotManager.ListSnapshotFiles().ToList();
    }

    private async Task RunScanAsync()
    {
        StatusText.Text = "Scanning host (OS, true CPU cores, NICs, native CIM/WMI, 64-bit Registry)...";
        try
        {
            _lastReport = await _scanner.ScanAsync();
            OsEnvironmentInfo os = _lastReport.OsInfo;
            HostSummaryText.Text = $"{os.ProductName} {os.DisplayVersion} (Build {os.BuildNumber})  •  {os.PhysicalCoreCount}C / {os.LogicalProcessorCount}T  •  BBR2: {(os.SupportsBbr2 ? "Supported" : "No")}";

            List<NetworkAdapterInfo> visible = _lastReport.Adapters
                .Where(a => !HostDiscoveryService.IsGhostSecondaryAdapter(a))
                .ToList();

            AdaptersGrid.ItemsSource = visible.Select(a => new
            {
                a.Alias,
                a.MediaKind,
                StatusText = a.IsUp ? "UP" : "DOWN",
                SpeedText = $"{a.LinkSpeedMbps} Mbps",
                a.Ipv4Mtu,
                Ipv4Display = a.Ipv4Address ?? "Not connected",
                a.Description
            }).ToList();

            AdapterCombo.ItemsSource = visible.Select(a => $"{a.Alias} ({a.LinkSpeedMbps} Mbps)").ToList();
            if (visible.Count > 0 && (AdapterCombo.SelectedIndex < 0 || AdapterCombo.SelectedIndex >= visible.Count))
            {
                _syncing = true;
                AdapterCombo.SelectedIndex = 0;
                MtuBox.Text = visible[0].Ipv4Mtu.ToString();
                PppoeCheck.IsChecked = visible[0].IsPppoe;
                _syncing = false;
            }

            ApplyProfileToControls(GetActiveProfile());
            int pending = _diffEngine.ComputeDiff(_lastReport, _customOverrides).Count;
            string alignText = pending == 0 ? "All settings aligned" : $"{pending} pending {(pending == 1 ? "change" : "changes")}";
            StatusText.Text = $"Ready: {visible.Count} adapter(s), {_lastReport.Parameters.Count} live parameters • {alignText}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Scan failed: {ex.Message}";
        }
    }

    private void ApplyProfileToControls(ProfileKind kind)
    {
        if (_lastReport is null) return;
        NetworkAdapterInfo? nic = GetSelectedAdapter();
        bool bbr2 = CmbCongestion.SelectedItem is ComboBoxItem ci && ci.Content?.ToString() == "BBR2";
        IReadOnlyDictionary<string, string> target = kind == ProfileKind.Custom
            ? _customOverrides
            : _profileEngine.BuildTargetValues(kind, _lastReport, nic, kind != ProfileKind.WindowsDefault && kind != ProfileKind.Current && bbr2);

        if (kind != ProfileKind.Custom)
        {
            _customOverrides = target.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        }

        _syncing = true;
        SelectCombo(CmbAutoTuning, Get("tcp.autotuning", "Normal"));
        SelectCombo(CmbHeuristics, Get("tcp.heuristics", "Disabled"));
        SelectCombo(CmbCongestion, Get("tcp.congestion", "CUBIC"));
        SelectCombo(CmbEcn, Get("tcp.ecn", "Disabled"));
        SelectCombo(CmbRss, Get("offload.rss", "Enabled"));
        SelectCombo(CmbRsc, Get("offload.rsc", "Enabled"));
        SelectCombo(CmbTimestamps, Get("tcp.timestamps", "Disabled"));
        SelectCombo(CmbTtl, Get("reg.default_ttl", "64"));
        _syncing = false;

        DiffGrid.ItemsSource = _diffEngine.ComputeDiff(_lastReport, _customOverrides, includeUnchanged: true);

        string Get(string k, string fb) => _customOverrides.TryGetValue(k, out string? v) ? v : fb;
    }

    private static void SelectCombo(ComboBox box, string value)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item && string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
    }

    private ProfileKind GetActiveProfile()
    {
        if (RadDefault.IsChecked == true) return ProfileKind.WindowsDefault;
        if (RadCurrent.IsChecked == true) return ProfileKind.Current;
        if (RadGaming.IsChecked == true) return ProfileKind.GamingLowLatency;
        if (RadCustom.IsChecked == true) return ProfileKind.Custom;
        return ProfileKind.RecommendedOptimal;
    }

    private NetworkAdapterInfo? GetSelectedAdapter()
    {
        if (_lastReport is null) return null;
        List<NetworkAdapterInfo> visible = _lastReport.Adapters
            .Where(a => !HostDiscoveryService.IsGhostSecondaryAdapter(a))
            .ToList();
        if (AdapterCombo.SelectedIndex >= 0 && AdapterCombo.SelectedIndex < visible.Count)
            return visible[AdapterCombo.SelectedIndex];
        return visible.FirstOrDefault(a => a.IsUp) ?? _lastReport.Adapters.FirstOrDefault(a => a.IsUp);
    }

    private void ProfileRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _lastReport is null) return;
        ApplyProfileToControls(GetActiveProfile());
    }

    private void InteractiveSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || sender is not ComboBox box || box.Tag is not string key) return;
        if (box.SelectedItem is ComboBoxItem item && item.Content?.ToString() is { } val)
        {
            _customOverrides[key] = val;
            if (key == "tcp.congestion" && _lastReport is not null)
            {
                _customOverrides["tcp.loopback_large_mtu"] =
                    string.Equals(val, "BBR2", StringComparison.OrdinalIgnoreCase) && _lastReport.OsInfo.RequiresBbr2LoopbackGuard
                        ? "Disabled"
                        : "Enabled";
            }

            _syncing = true;
            RadCustom.IsChecked = true;
            _syncing = false;
            ApplyProfileToControls(ProfileKind.Custom);
        }
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeedLabelText is null) return;
        int mbps = (int)e.NewValue;
        BdpResult bdp = BdpCalculator.Calculate(mbps, BandwidthUnit.Mbps, 40, 1500);
        SpeedLabelText.Text = mbps >= 1000
            ? $"1+ Gbps  •  RWIN {bdp.SuggestedRwinBytes / 1024.0:N0} KB"
            : $"{mbps} Mbps  •  RWIN {bdp.SuggestedRwinBytes / 1024.0:N0} KB";
    }

    private void AdapterCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        NetworkAdapterInfo? nic = GetSelectedAdapter();
        if (nic is null) return;
        _syncing = true;
        MtuBox.Text = nic.Ipv4Mtu.ToString();
        PppoeCheck.IsChecked = nic.IsPppoe;
        _syncing = false;
        ApplyProfileToControls(GetActiveProfile());
    }

    private void PppoeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        MtuBox.Text = PppoeCheck.IsChecked == true ? "1492" : "1500";
        NetworkAdapterInfo? nic = GetSelectedAdapter();
        if (nic is not null)
        {
            _customOverrides[$"nic.{nic.InterfaceGuid}.mtu"] = MtuBox.Text;
            _syncing = true;
            RadCustom.IsChecked = true;
            _syncing = false;
            ApplyProfileToControls(ProfileKind.Custom);
        }
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        RadDefault.IsChecked = true;
        ApplyProfileToControls(ProfileKind.WindowsDefault);
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await RunScanAsync();

    private async void PreviewSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null) return;
        IReadOnlyList<ChangePlanItem> diffs = _diffEngine.ComputeDiff(_lastReport, _customOverrides);
        if (diffs.Count == 0)
        {
            StatusText.Text = "No changes needed: current settings already match your selection.";
            return;
        }

        string profileName = GetActiveProfile().ToString();
        var dlg = new DiffPreviewWindow(profileName, diffs) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (dlg.SelectedAction == DiffModalAction.ExportScripts)
        {
            ExportScripts(diffs, profileName);
            return;
        }

        if (dlg.SelectedAction == DiffModalAction.ApplyUac)
        {
            await ApplyChangesNativeOrElevatedAsync(diffs, profileName);
        }
    }

    private async Task ApplyChangesNativeOrElevatedAsync(IReadOnlyList<ChangePlanItem> diffs, string profileName)
    {
        if (_lastReport is null) return;

        var manifest = new SnapshotManifest(
            $"snap-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
            DateTimeOffset.UtcNow,
            _lastReport.OsInfo.BuildNumber.ToString(),
            profileName,
            diffs);

        if (ElevationLauncher.IsProcessElevated())
        {
            StatusText.Text = $"Applying {diffs.Count} change(s) directly via native .NET...";
            await _snapshotManager.SaveSnapshotAsync(manifest);
            ApplyReport report = await _applyEngine.ApplyPlanAsync(manifest.Items);
            await RunScanAsync();

            if (report.AllSucceeded)
            {
                StatusText.Text = $"Applied {diffs.Count} setting(s) natively via .NET (snapshot saved).";
            }
            else
            {
                string failed = string.Join(" | ", report.Results.Where(r => !r.Succeeded).Take(3).Select(r => $"{r.ParameterId}: {r.Message}"));
                StatusText.Text = $"Partial failure: {failed}";
            }
            return;
        }

        string? cli = ElevationLauncher.ResolveCliPath();
        if (cli is null)
        {
            StatusText.Text = "FluentNet.Cli.exe not found beside FluentNet.Classic.exe — run as Administrator or export .ps1/.reg.";
            return;
        }

        string tempPlan = Path.Combine(Path.GetTempPath(), $"fluentnet-plan-{Guid.NewGuid():N}.json");
        File.WriteAllText(tempPlan, SnapshotManager.SerializeManifest(manifest));
        try
        {
            StatusText.Text = $"Requesting UAC to apply {diffs.Count} change(s) natively...";
            int code = await Task.Run(() => ElevationLauncher.RunElevatedCli(cli, $"apply --plan \"{tempPlan}\"", hideWindow: true));
            await RunScanAsync();
            StatusText.Text = code == 0
                ? $"Applied {diffs.Count} setting(s) via native .NET worker (pre-change snapshot saved)."
                : $"Elevated worker exited with code {code}.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "UAC prompt cancelled — no changes were made.";
        }
        finally
        {
            try { if (File.Exists(tempPlan)) File.Delete(tempPlan); } catch { /* ignore */ }
        }
    }

    private void ExportScripts_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null) return;
        ExportScripts(_diffEngine.ComputeDiff(_lastReport, _customOverrides), GetActiveProfile().ToString());
    }

    private void ExportScripts(IReadOnlyList<ChangePlanItem> diffs, string profileName)
    {
        string outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "FluentNet-Exports");
        Directory.CreateDirectory(outDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        File.WriteAllText(Path.Combine(outDir, $"FluentNet_{profileName}_{stamp}.ps1"), ScriptExporter.ExportPowerShellApplyScript(diffs, profileName));
        File.WriteAllText(Path.Combine(outDir, $"FluentNet_{profileName}_{stamp}.reg"), ScriptExporter.ExportRegistryFile(diffs));
        StatusText.Text = $"Exported .ps1 and .reg to {outDir}";
    }

    private void CalcBdp_Click(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(BdpBwBox.Text, out double bw) && double.TryParse(BdpMsBox.Text, out double ms) && int.TryParse(BdpMtuBox.Text, out int mtu))
        {
            BdpResult r = BdpCalculator.Calculate(bw, BandwidthUnit.Mbps, ms, mtu);
            BdpResultText.Text = $"BDP: {r.BdpBytes:N0} B | MSS: {r.Mss} B | RWIN: {r.SuggestedRwinBytes:N0} B (Scale {r.RecommendedScaleFactor})";
        }
    }

    private async void RunMtu_Click(object sender, RoutedEventArgs e)
    {
        string host = HostsBox.Text.Split(',')[0].Trim();
        await RunDiagAsync(async ct => (await _icmp.DiscoverLargestMtuAsync(host, cancellationToken: ct)).StatusMessage);
    }

    private async void RunLatency_Click(object sender, RoutedEventArgs e)
    {
        string[] hosts = HostsBox.Text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        await RunDiagAsync(async ct =>
        {
            IReadOnlyList<LatencySweepResult> list = await _icmp.MeasureLatencySweepAsync(hosts, cancellationToken: ct);
            return string.Join(Environment.NewLine, list.Select(r => $"{r.Host,-22} Loss:{r.PacketLossPercent}% Min:{r.MinMs}ms Avg:{r.AvgMs}ms Max:{r.MaxMs}ms Jitter:{r.JitterMs}ms"));
        });
    }

    private async void RunTrace_Click(object sender, RoutedEventArgs e)
    {
        string host = HostsBox.Text.Split(',')[0].Trim();
        await RunDiagAsync(async ct =>
        {
            IReadOnlyList<TraceHopResult> hops = await _icmp.TraceRouteAsync(host, cancellationToken: ct);
            return string.Join(Environment.NewLine, hops.Select(h => $"Hop {h.Hop,2}: {h.Address,-16} {h.RoundTripMs,4} ms ({h.Status})"));
        });
    }

    private async Task RunDiagAsync(Func<CancellationToken, Task<string>> action)
    {
        _diagCts?.Cancel();
        _diagCts = new CancellationTokenSource();
        StopBtn.IsEnabled = true;
        try
        {
            string res = await action(_diagCts.Token);
            DiagBox.Text = res + Environment.NewLine + Environment.NewLine + DiagBox.Text;
        }
        catch (OperationCanceledException)
        {
            DiagBox.Text = "[Cancelled]" + Environment.NewLine + DiagBox.Text;
        }
        finally
        {
            StopBtn.IsEnabled = false;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _diagCts?.Cancel();
    private void Clear_Click(object sender, RoutedEventArgs e) => DiagBox.Text = string.Empty;
    private void RefreshSnaps_Click(object sender, RoutedEventArgs e) => SnapshotsList.ItemsSource = _snapshotManager.ListSnapshotFiles().ToList();

    private async void RollbackSnap_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsList.SelectedItem is not string snapPath) return;

        if (ElevationLauncher.IsProcessElevated())
        {
            SnapshotManifest? snap = await _snapshotManager.LoadSnapshotAsync(snapPath);
            if (snap is null || snap.Items.Count == 0) return;
            List<ChangePlanItem> revert = snap.Items.Select(i => i with { OldValue = i.NewValue, NewValue = i.OldValue }).ToList();
            ApplyReport res = await _applyEngine.ApplyPlanAsync(revert);
            StatusText.Text = res.AllSucceeded ? "Rollback complete (native .NET)." : "Rollback finished with errors.";
            await RunScanAsync();
            return;
        }

        string? cli = ElevationLauncher.ResolveCliPath();
        if (cli is null) return;
        int code = await Task.Run(() => ElevationLauncher.RunElevatedCli(cli, $"rollback --snapshot \"{snapPath}\"", hideWindow: true));
        StatusText.Text = code == 0 ? "Rollback complete." : $"Rollback exited with code {code}.";
        await RunScanAsync();
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        AppLinks.OpenInBrowser(AppLinks.GitHubRepo);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Checking GitHub Releases for FluentNet updates...";
        UpdateCheckResult res = await _updater.CheckForUpdatesAsync(_appVersion, UpdateEdition.ClassicWpf);
        StatusText.Text = res.StatusMessage;

        if (res.Status == UpdateCheckStatus.UpdateAvailable)
        {
            MessageBoxResult answer = MessageBox.Show(
                this,
                $"{res.StatusMessage}\n\nClick YES to download & verify the latest zip package, or NO to open GitHub Releases.",
                $"FluentNet v{res.LatestVersion} Available",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Information);

            if (answer == MessageBoxResult.Yes && !string.IsNullOrWhiteSpace(res.AssetDownloadUrl))
            {
                UpdateDownloadResult dl = await _updater.DownloadReleasePackageAsync(res);
                StatusText.Text = dl.Message;
                if (dl.Succeeded && !string.IsNullOrWhiteSpace(dl.LocalFilePath))
                {
                    AppLinks.OpenFolder(Path.GetDirectoryName(dl.LocalFilePath)!);
                }
            }
            else if (answer == MessageBoxResult.No)
            {
                AppLinks.OpenInBrowser(res.ReleasePageUrl);
            }
        }
        else
        {
            MessageBox.Show(
                this,
                $"{res.StatusMessage}\n\nDeveloper: {AppLinks.DeveloperName} ({AppLinks.OrganizationName})\nGitHub: {AppLinks.GitHubRepo}",
                "FluentNet — Version Check & Credits",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}
