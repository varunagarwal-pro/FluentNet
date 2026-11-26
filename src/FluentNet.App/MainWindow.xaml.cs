using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FluentNet.Core.Models;
using FluentNet.Core.Serialization;
using FluentNet.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FluentNet.App;

public sealed record AdapterDisplayRow(
    string Alias,
    string Description,
    string SpeedAndMtu,
    string IpAddress,
    string StatusLabel,
    Brush StatusBg,
    Brush StatusFg,
    string IconPath);

public sealed record DriverPropRow(
    string DisplayName,
    string RegistryKeyword,
    string DisplayValue);

public sealed partial class MainWindow : Window
{
    private readonly HostScanner _scanner = new();
    private readonly ProfileEngine _profileEngine = new();
    private readonly DiffEngine _diffEngine = new();
    private readonly SnapshotManager _snapshotManager = new();
    private readonly IcmpDiagnosticsService _icmp = new();
    private readonly UpdateService _updater = new();

    private HostScanReport? _lastReport;
    private UpdateCheckResult? _lastUpdateCheck;
    private Dictionary<string, string> _customOverrides = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _diagCts;
    private bool _syncingControls;
    private string _searchQuery = string.Empty;
    private readonly string _appVersion;

    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
    private static readonly Brush UpBgBrush = new SolidColorBrush(Color.FromArgb(40, 16, 185, 129));
    private static readonly Brush DownBgBrush = new SolidColorBrush(Color.FromArgb(40, 148, 163, 184));

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        InitializeDropdownSources();
        SnapshotFolderText.Text = _snapshotManager.SnapshotDirectory;
        _appVersion = UpdateService.GetInstalledVersion(Assembly.GetExecutingAssembly());
        AppTitleBar.Subtitle = $"64-Bit Network Stack Optimizer • v{_appVersion}";
        UpdateNavTitle.Text = $"v{_appVersion} • Check Updates";
        AboutVersionText.Text = $"FluentNet v{_appVersion} • 64-bit Windows Network Stack Optimizer • MIT Open Source • Zero Telemetry";

        _ = RunHostScanAsync();
        _ = CheckForUpdatesBackgroundAsync();
    }

    private void InitializeDropdownSources()
    {
        _syncingControls = true;
        CmbAutoTuning.ItemsSource = new[] { "Normal", "Disabled", "HighlyRestricted", "Restricted", "Experimental" };
        CmbHeuristics.ItemsSource = new[] { "Disabled", "Enabled" };
        CmbCongestion.ItemsSource = new[] { "CUBIC", "CTCP", "BBR2", "Default" };
        CmbEcn.ItemsSource = new[] { "Default", "Enabled", "Disabled" };
        CmbRss.ItemsSource = new[] { "Enabled", "Disabled" };
        CmbRsc.ItemsSource = new[] { "Enabled", "Disabled" };
        CmbTimestamps.ItemsSource = new[] { "Disabled", "Enabled", "Default" };
        CmbTtl.ItemsSource = new[] { "64", "128", "255" };

        CmbThrottling.ItemsSource = new[] { "0xffffffff", "10", "30" };
        CmbResponsiveness.ItemsSource = new[] { "0", "10", "20" };
        CmbAckFreq.ItemsSource = new[] { "1", "2" };
        CmbNoDelay.ItemsSource = new[] { "0", "1" };
        CmbDelAckTicks.ItemsSource = new[] { "0", "1", "2" };
        CmbMaxSyn.ItemsSource = new[] { "2", "3", "4" };
        CmbNonSack.ItemsSource = new[] { "Disabled", "Enabled" };
        CmbInitialRto.ItemsSource = new[] { "1000", "2000", "3000" };
        CmbMinRto.ItemsSource = new[] { "300", "200", "100" };
        CmbMaxUserPort.ItemsSource = new[] { "65534", "49152-65535 (Dynamic)" };
        CmbTimedWait.ItemsSource = new[] { "30", "60", "120" };
        CmbQosLimit.ItemsSource = new[] { "0", "20" };
        CmbQosNla.ItemsSource = new[] { "1", "0" };

        CmbTheme.ItemsSource = new[] { "System Default", "Dark", "Light" };
        CmbTheme.SelectedIndex = 0;
        _syncingControls = false;
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args) => NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void AccelFocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        TitleSearchBox.Focus(FocusState.Programmatic);
        args.Handled = true;
    }

    private void TitleSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _searchQuery = sender.Text.Trim();
        RefreshAdaptersView();
        RefreshDriverProperties();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string tag = args.IsSettingsSelected
            ? "app_settings"
            : (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "dashboard";

        DashboardPanel.Visibility = tag == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        GeneralPanel.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPanel.Visibility = tag == "advanced" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPanel.Visibility = tag == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        SnapshotsPanel.Visibility = tag == "snapshots" ? Visibility.Visible : Visibility.Collapsed;
        AppSettingsPanel.Visibility = tag == "app_settings" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "snapshots")
        {
            SnapshotsList.ItemsSource = _snapshotManager.ListSnapshotFiles().ToList();
        }
    }

    private async Task RunHostScanAsync()
    {
        OverviewSubtitleText.Text = "Scanning OS build, network adapters, CIM/WMI TCP templates, and 64-bit Registry...";
        StatusBanner.IsOpen = false;

        try
        {
            _lastReport = await _scanner.ScanAsync();
            OsEnvironmentInfo os = _lastReport.OsInfo;

            KpiOsText.Text = $"{os.ProductName} {os.DisplayVersion}";
            KpiCpuText.Text = $"Build {os.BuildNumber} • {os.PhysicalCoreCount}C / {os.LogicalProcessorCount}T";

            NetworkAdapterInfo? primary = _lastReport.Adapters.FirstOrDefault(a => a.IsUp);
            KpiNicText.Text = primary?.Alias ?? "None Active";
            KpiLinkText.Text = primary is not null ? $"{primary.LinkSpeedMbps} Mbps • MTU {primary.Ipv4Mtu}" : "—";

            string? cong = _lastReport.Parameters.FirstOrDefault(p => p.Id == "tcp.congestion")?.CurrentValue;
            KpiCongestionText.Text = cong ?? "CUBIC";
            KpiBbrGuardText.Text = os.RequiresBbr2LoopbackGuard ? "Win11 24H2+ Guard Ready" : "Standard Stack";

            OverviewSubtitleText.Text = $"64-bit unelevated scan • {_lastReport.Parameters.Count} live parameters inspected • Mandatory JSON snapshot before any change";

            RefreshAdaptersView();
            ApplyProfileToControls(GetActiveProfileKind());
        }
        catch (Exception ex)
        {
            StatusBanner.Severity = InfoBarSeverity.Error;
            StatusBanner.Title = "Host scan failed";
            StatusBanner.Message = ex.Message;
            StatusBanner.IsOpen = true;
        }
    }

    private IReadOnlyList<NetworkAdapterInfo> GetVisibleAdapters()
    {
        if (_lastReport is null) return Array.Empty<NetworkAdapterInfo>();
        if (ToggleVirtualAdapters.IsOn) return _lastReport.Adapters;

        return _lastReport.Adapters
            .Where(a => a.MediaKind is AdapterMediaKind.Ethernet or AdapterMediaKind.WiFi)
            .Where(a => a.IsUp || a.LinkSpeedMbps > 0 || !(a.Ipv4Address?.StartsWith("169.254.", StringComparison.Ordinal) ?? false) || a.Alias.Equals("Ethernet", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void RefreshAdaptersView()
    {
        IReadOnlyList<NetworkAdapterInfo> visible = GetVisibleAdapters();

        _syncingControls = true;
        AdapterCombo.ItemsSource = visible
            .Select(a => $"{a.Alias} ({a.LinkSpeedMbps} Mbps{(string.IsNullOrEmpty(a.Ipv4Address) ? "" : $" • {a.Ipv4Address}")})")
            .ToList();
        if (visible.Count > 0 && (AdapterCombo.SelectedIndex < 0 || AdapterCombo.SelectedIndex >= visible.Count))
        {
            AdapterCombo.SelectedIndex = 0;
            MtuBox.Text = visible[0].Ipv4Mtu.ToString();
            PppoeCheck.IsChecked = visible[0].IsPppoe;
        }
        _syncingControls = false;

        IEnumerable<NetworkAdapterInfo> query = visible;
        if (!string.IsNullOrEmpty(_searchQuery))
        {
            query = query.Where(a =>
                a.Alias.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                a.Description.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));
        }

        List<AdapterDisplayRow> rows = query.Select(a =>
        {
            string iconPath = a.MediaKind switch
            {
                AdapterMediaKind.WiFi => "Assets/icons/wifi.png",
                AdapterMediaKind.Ethernet => "Assets/icons/ethernet.png",
                _ => "Assets/icons/adapter_other.png"
            };

            return new AdapterDisplayRow(
                Alias: a.Alias,
                Description: a.Description,
                SpeedAndMtu: $"{a.LinkSpeedMbps} Mbps  •  MTU {a.Ipv4Mtu}",
                IpAddress: a.Ipv4Address ?? "Not connected",
                StatusLabel: a.IsUp ? "UP" : "DOWN",
                StatusBg: a.IsUp ? UpBgBrush : DownBgBrush,
                StatusFg: a.IsUp ? GreenBrush : MutedBrush,
                IconPath: iconPath);
        }).ToList();

        AdaptersRepeater.ItemsSource = rows;
        AdaptersCountText.Text = $"{rows.Count} shown ({_lastReport?.Adapters.Count ?? 0} total)";
        RefreshDriverProperties();
    }

    private void RefreshDriverProperties()
    {
        NetworkAdapterInfo? nic = GetSelectedAdapter();
        if (nic is null)
        {
            DriverPropsHeader.Text = "Selected Adapter Driver Properties (None)";
            DriverPropsRepeater.ItemsSource = Array.Empty<DriverPropRow>();
            return;
        }

        DriverPropsHeader.Text = $"Selected Adapter Driver Properties ({nic.Alias})";
        IEnumerable<AdapterAdvancedPropertyInfo> props = nic.AdvancedProperties;
        if (!string.IsNullOrEmpty(_searchQuery))
        {
            props = props.Where(p =>
                p.DisplayName.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                p.RegistryKeyword.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));
        }

        DriverPropsRepeater.ItemsSource = props
            .Select(p => new DriverPropRow(p.DisplayName, p.RegistryKeyword, p.DisplayValue))
            .ToList();
    }

    private void ApplyProfileToControls(ProfileKind kind)
    {
        if (_lastReport is null) return;

        NetworkAdapterInfo? nic = GetSelectedAdapter();
        bool bbr2 = string.Equals(CmbCongestion.SelectedItem?.ToString(), "BBR2", StringComparison.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, string> target = kind == ProfileKind.Custom
            ? _customOverrides
            : _profileEngine.BuildTargetValues(kind, _lastReport, nic, preferBbr2: kind != ProfileKind.WindowsDefault && kind != ProfileKind.Current && bbr2);

        if (kind != ProfileKind.Custom)
        {
            _customOverrides = new Dictionary<string, string>(target, StringComparer.OrdinalIgnoreCase);
        }

        _syncingControls = true;
        SelectItem(CmbAutoTuning, GetTarget("tcp.autotuning", "Normal"));
        SelectItem(CmbHeuristics, GetTarget("tcp.heuristics", "Disabled"));
        SelectItem(CmbCongestion, GetTarget("tcp.congestion", "CUBIC"));
        SelectItem(CmbEcn, GetTarget("tcp.ecn", "Default"));
        SelectItem(CmbRss, GetTarget("offload.rss", "Enabled"));
        SelectItem(CmbRsc, GetTarget("offload.rsc", "Enabled"));
        SelectItem(CmbTimestamps, GetTarget("tcp.timestamps", "Disabled"));
        SelectItem(CmbTtl, GetTarget("reg.default_ttl", "64"));

        SelectItem(CmbThrottling, GetTarget("reg.network_throttling", "0xffffffff"));
        SelectItem(CmbResponsiveness, GetTarget("reg.system_responsiveness", "10"));
        SelectItem(CmbMaxSyn, GetTarget("tcp.maxsynretx", "2"));
        SelectItem(CmbNonSack, GetTarget("tcp.nonsackrtt", "Disabled"));
        SelectItem(CmbInitialRto, GetTarget("tcp.initialrto", "2000"));
        SelectItem(CmbMinRto, GetTarget("tcp.minrto", "300"));
        SelectItem(CmbMaxUserPort, GetTarget("reg.max_user_port", "65534"));
        SelectItem(CmbTimedWait, GetTarget("reg.tcp_timed_wait_delay", "30"));
        SelectItem(CmbQosLimit, GetTarget("reg.qos_non_best_effort", "0"));
        SelectItem(CmbQosNla, GetTarget("reg.qos_do_not_use_nla", "1"));

        if (nic is not null)
        {
            SelectItem(CmbAckFreq, GetTarget($"nic.{nic.InterfaceGuid}.tcpackfreq", "2"));
            SelectItem(CmbNoDelay, GetTarget($"nic.{nic.InterfaceGuid}.tcpnodelay", "0"));
            SelectItem(CmbDelAckTicks, GetTarget($"nic.{nic.InterfaceGuid}.tcpdelackticks", "2"));
        }
        _syncingControls = false;

        UpdatePendingSummary(kind);

        string GetTarget(string key, string fallback) => _customOverrides.TryGetValue(key, out string? v) ? v : fallback;
    }

    private void UpdatePendingSummary(ProfileKind kind)
    {
        if (_lastReport is null) return;
        int diffCount = _diffEngine.ComputeDiff(_lastReport, _customOverrides).Count;
        string friendly = kind switch
        {
            ProfileKind.RecommendedOptimal => "Optimal",
            ProfileKind.GamingLowLatency => "Gaming",
            ProfileKind.WindowsDefault => "Windows Default",
            _ => kind.ToString()
        };

        KpiDriftText.Text = diffCount == 0 ? "Aligned (0)" : $"{diffCount} Changes";
        KpiProfileLabel.Text = $"Profile: {friendly}";
        ApplyChangesButton.Content = diffCount == 0 ? "Apply Changes..." : $"Apply {diffCount} Change(s)...";
    }

    private static void SelectItem(ComboBox box, string value)
    {
        if (box.ItemsSource is IEnumerable<string> items)
        {
            foreach (string s in items)
            {
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedItem = s;
                    return;
                }
            }
        }
    }

    private ProfileKind GetActiveProfileKind()
    {
        if (RadDefault.IsChecked == true) return ProfileKind.WindowsDefault;
        if (RadCurrent.IsChecked == true) return ProfileKind.Current;
        if (RadGaming.IsChecked == true) return ProfileKind.GamingLowLatency;
        if (RadCustom.IsChecked == true) return ProfileKind.Custom;
        return ProfileKind.RecommendedOptimal;
    }

    private NetworkAdapterInfo? GetSelectedAdapter()
    {
        IReadOnlyList<NetworkAdapterInfo> visible = GetVisibleAdapters();
        if (AdapterCombo.SelectedIndex >= 0 && AdapterCombo.SelectedIndex < visible.Count)
        {
            return visible[AdapterCombo.SelectedIndex];
        }
        return visible.FirstOrDefault(a => a.IsUp) ?? _lastReport?.Adapters.FirstOrDefault(a => a.IsUp);
    }

    private void ProfileRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingControls || _lastReport is null) return;
        ApplyProfileToControls(GetActiveProfileKind());
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        RadDefault.IsChecked = true;
        ApplyProfileToControls(ProfileKind.WindowsDefault);
    }

    private void InteractiveCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || sender is not ComboBox box || box.Tag is not string key || box.SelectedItem is not string val) return;

        _customOverrides[key] = val;
        if (key == "tcp.congestion" && _lastReport is not null)
        {
            _customOverrides["tcp.loopback_large_mtu"] =
                string.Equals(val, "BBR2", StringComparison.OrdinalIgnoreCase) && _lastReport.OsInfo.RequiresBbr2LoopbackGuard
                    ? "disable"
                    : "enable";
        }

        _syncingControls = true;
        RadCustom.IsChecked = true;
        _syncingControls = false;
        UpdatePendingSummary(ProfileKind.Custom);
    }

    private void InteractiveNicCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || sender is not ComboBox box || box.Tag is not string suffix || box.SelectedItem is not string val) return;
        NetworkAdapterInfo? nic = GetSelectedAdapter();
        if (nic is null) return;

        string key = suffix.Replace("nic.", $"nic.{nic.InterfaceGuid}.");
        _customOverrides[key] = val;

        _syncingControls = true;
        RadCustom.IsChecked = true;
        _syncingControls = false;
        UpdatePendingSummary(ProfileKind.Custom);
    }

    private void SpeedSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (SpeedLabelText is null) return;
        SpeedLabelText.Text = e.NewValue >= 1000 ? "1+ Gbps" : $"{(int)e.NewValue} Mbps";
    }

    private void AdapterCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls) return;
        NetworkAdapterInfo? nic = GetSelectedAdapter();
        if (nic is null) return;
        _syncingControls = true;
        MtuBox.Text = nic.Ipv4Mtu.ToString();
        PppoeCheck.IsChecked = nic.IsPppoe;
        _syncingControls = false;
        RefreshDriverProperties();
    }

    private void PppoeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        MtuBox.Text = PppoeCheck.IsChecked == true ? "1492" : "1500";
    }

    private async void RescanButton_Click(object sender, RoutedEventArgs e) => await RunHostScanAsync();

    private async void PreviewSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null) return;
        IReadOnlyList<ChangePlanItem> diffs = _diffEngine.ComputeDiff(_lastReport, _customOverrides);
        if (diffs.Count == 0)
        {
            StatusBanner.Severity = InfoBarSeverity.Success;
            StatusBanner.Title = "No changes needed";
            StatusBanner.Message = "Current host settings already match your selected configuration.";
            StatusBanner.IsOpen = true;
            return;
        }

        string friendly = KpiProfileLabel.Text.Replace("Profile: ", string.Empty);
        var dialog = new ReviewChangesDialog(friendly, diffs)
        {
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            IReadOnlyList<ChangePlanItem> selected = dialog.GetSelectedItems();
            if (selected.Count > 0)
            {
                await ApplyWithUacAsync(selected, friendly);
            }
        }
    }

    private async void ExportScripts_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null) return;
        IReadOnlyList<ChangePlanItem> diffs = _diffEngine.ComputeDiff(_lastReport, _customOverrides);
        string profileName = GetActiveProfileKind().ToString();
        string outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "FluentNet-Exports");
        Directory.CreateDirectory(outDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        await File.WriteAllTextAsync(Path.Combine(outDir, $"FluentNet_{profileName}_{stamp}.ps1"), ScriptExporter.ExportPowerShellApplyScript(diffs, profileName));
        await File.WriteAllTextAsync(Path.Combine(outDir, $"FluentNet_{profileName}_{stamp}.reg"), ScriptExporter.ExportRegistryFile(diffs));
        StatusBanner.Severity = InfoBarSeverity.Success;
        StatusBanner.Title = "Scripts exported";
        StatusBanner.Message = $"Saved .ps1 and .reg to {outDir}";
        StatusBanner.IsOpen = true;
    }

    private async Task ApplyWithUacAsync(IReadOnlyList<ChangePlanItem> diffs, string profileName)
    {
        if (_lastReport is null) return;
        string? cli = ElevationLauncher.ResolveCliPath();
        if (cli is null)
        {
            StatusBanner.Severity = InfoBarSeverity.Warning;
            StatusBanner.Title = "FluentNet.Cli.exe not found";
            StatusBanner.Message = "Place FluentNet.Cli.exe beside FluentNet.exe or export .ps1/.reg scripts.";
            StatusBanner.IsOpen = true;
            return;
        }

        var manifest = new SnapshotManifest($"snap-{DateTime.UtcNow:yyyyMMdd-HHmmss}", DateTimeOffset.UtcNow, _lastReport.OsInfo.BuildNumber.ToString(), profileName, diffs);
        string tempPlan = Path.Combine(Path.GetTempPath(), $"fluentnet-plan-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempPlan, JsonSerializer.Serialize(manifest, FluentNetJsonContext.Default.SnapshotManifest));

        int code = await Task.Run(() => ElevationLauncher.RunElevatedCli(cli, $"apply --plan \"{tempPlan}\""));
        await RunHostScanAsync();
        StatusBanner.Severity = code == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        StatusBanner.Title = code == 0 ? "Changes applied (pre-change snapshot saved)" : $"Apply exited with code {code}";
        StatusBanner.Message = code == 0 ? "All selected parameters were applied safely." : "One or more settings failed to apply.";
        StatusBanner.IsOpen = true;
    }

    private void CalculateBdp_Click(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(BdpBandwidthBox.Text, out double bw) &&
            double.TryParse(BdpLatencyBox.Text, out double ms) &&
            int.TryParse(BdpMtuBox.Text, out int mtu))
        {
            BdpResult r = BdpCalculator.Calculate(bw, BandwidthUnit.Mbps, ms, mtu);
            BdpResultText.Text = $"BDP: {r.BdpBytes:N0} B  •  MSS: {r.Mss} B  •  RWIN: {r.SuggestedRwinBytes:N0} B (Scale {r.RecommendedScaleFactor})";
        }
    }

    private async void RunMtu_Click(object sender, RoutedEventArgs e)
    {
        string host = DiagHostsBox.Text.Split(',')[0].Trim();
        await RunDiagnosticAsync(async ct => $"[MTU] {host}: {(await _icmp.DiscoverLargestMtuAsync(host, cancellationToken: ct)).StatusMessage}");
    }

    private async void RunLatency_Click(object sender, RoutedEventArgs e)
    {
        string[] hosts = DiagHostsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries);
        await RunDiagnosticAsync(async ct =>
        {
            IReadOnlyList<LatencySweepResult> results = await _icmp.MeasureLatencySweepAsync(hosts, cancellationToken: ct);
            return string.Join(Environment.NewLine, results.Select(r =>
                $"[LATENCY] {r.Host,-22} | Loss:{r.PacketLossPercent}% | Min:{r.MinMs}ms Avg:{r.AvgMs}ms Max:{r.MaxMs}ms Jitter:{r.JitterMs}ms"));
        });
    }

    private async void RunTrace_Click(object sender, RoutedEventArgs e)
    {
        string host = DiagHostsBox.Text.Split(',')[0].Trim();
        await RunDiagnosticAsync(async ct =>
        {
            IReadOnlyList<TraceHopResult> hops = await _icmp.TraceRouteAsync(host, cancellationToken: ct);
            return string.Join(Environment.NewLine, hops.Select(h => $"Hop {h.Hop,2}: {h.Address,-16} {h.RoundTripMs,4} ms ({h.Status})"));
        });
    }

    private async Task RunDiagnosticAsync(Func<CancellationToken, Task<string>> action)
    {
        _diagCts?.Cancel();
        _diagCts = new CancellationTokenSource();
        StopDiagButton.IsEnabled = true;
        try
        {
            string output = await action(_diagCts.Token);
            DiagOutputBox.Text = output + Environment.NewLine + Environment.NewLine + DiagOutputBox.Text;
        }
        catch (OperationCanceledException)
        {
            DiagOutputBox.Text = "[Cancelled by user]" + Environment.NewLine + DiagOutputBox.Text;
        }
        finally
        {
            StopDiagButton.IsEnabled = false;
        }
    }

    private void StopDiag_Click(object sender, RoutedEventArgs e) => _diagCts?.Cancel();
    private void ClearDiag_Click(object sender, RoutedEventArgs e) => DiagOutputBox.Text = string.Empty;
    private void RefreshSnapshots_Click(object sender, RoutedEventArgs e) => SnapshotsList.ItemsSource = _snapshotManager.ListSnapshotFiles().ToList();

    private async void RollbackSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsList.SelectedItem is not string snapPath) return;
        string? cli = ElevationLauncher.ResolveCliPath();
        if (cli is null) return;
        int code = await Task.Run(() => ElevationLauncher.RunElevatedCli(cli, $"rollback --snapshot \"{snapPath}\""));
        await RunHostScanAsync();
        StatusBanner.Severity = code == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        StatusBanner.Title = code == 0 ? "Rollback complete" : $"Rollback exited with code {code}";
        StatusBanner.IsOpen = true;
    }

    private void ThemeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || Content is not FrameworkElement root) return;
        root.RequestedTheme = CmbTheme.SelectedIndex switch
        {
            1 => ElementTheme.Dark,
            2 => ElementTheme.Light,
            _ => ElementTheme.Default
        };
    }

    private void VirtualAdapters_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingControls || _lastReport is null) return;
        RefreshAdaptersView();
    }

    private void OpenSnapshotsFolder_Click(object sender, RoutedEventArgs e)
    {
        AppLinks.OpenFolder(_snapshotManager.SnapshotDirectory);
    }

    private async void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string tag && tag == "check_updates")
        {
            await CheckForUpdatesInteractiveAsync();
        }
    }

    private async Task CheckForUpdatesBackgroundAsync()
    {
        UpdateCheckResult res = await _updater.CheckForUpdatesAsync(_appVersion, UpdateEdition.WinUI3);
        _lastUpdateCheck = res;
        DispatcherQueue.TryEnqueue(() => ApplyUpdateUiState(res, showDialogOnNew: false));
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesInteractiveAsync();
    }

    private async Task CheckForUpdatesInteractiveAsync()
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking GitHub Releases for latest FluentNet build...";
        try
        {
            UpdateCheckResult res = await _updater.CheckForUpdatesAsync(_appVersion, UpdateEdition.WinUI3);
            _lastUpdateCheck = res;
            ApplyUpdateUiState(res, showDialogOnNew: true);
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void ApplyUpdateUiState(UpdateCheckResult res, bool showDialogOnNew)
    {
        UpdateStatusText.Text = res.StatusMessage;

        if (!string.IsNullOrWhiteSpace(res.ReleaseNotes))
        {
            UpdateNotesText.Text = res.ReleaseNotes;
            UpdateNotesText.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateNotesText.Visibility = Visibility.Collapsed;
        }

        if (res.Status == UpdateCheckStatus.UpdateAvailable)
        {
            UpdateNavTitle.Text = $"Update v{res.LatestVersion} Ready";
            UpdateNavSub.Text = "Click to review & download";
            UpdateNavIcon.Foreground = GreenBrush;
            DownloadUpdateButton.Content = string.IsNullOrWhiteSpace(res.AssetDownloadUrl) ? "Open Release" : "Download Update";

            if (!showDialogOnNew && Content?.XamlRoot is null) return;
            if (showDialogOnNew && Content?.XamlRoot is not null)
            {
                string body = string.IsNullOrWhiteSpace(res.ReleaseNotes)
                    ? $"Installed: v{res.CurrentVersion}\nAvailable: v{res.LatestVersion}\n\nPublished by {AppLinks.DeveloperName} ({AppLinks.OrganizationName})."
                    : $"Installed: v{res.CurrentVersion}  ->  Available: v{res.LatestVersion}\n\n{res.ReleaseNotes}";

                var dlg = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    RequestedTheme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
                    Title = $"FluentNet v{res.LatestVersion} Available",
                    Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
                    PrimaryButtonText = string.IsNullOrWhiteSpace(res.AssetDownloadUrl) ? "Open GitHub Release" : "Download & Verify (.zip)",
                    SecondaryButtonText = "View on GitHub",
                    CloseButtonText = "Later",
                    DefaultButton = ContentDialogButton.Primary
                };

                ContentDialogResult choice = await dlg.ShowAsync();
                if (choice == ContentDialogResult.Primary)
                {
                    await DownloadOrOpenReleaseAsync(res);
                }
                else if (choice == ContentDialogResult.Secondary)
                {
                    AppLinks.OpenInBrowser(res.ReleasePageUrl);
                }
            }
        }
        else if (showDialogOnNew && Content?.XamlRoot is not null)
        {
            UpdateNavTitle.Text = $"v{_appVersion} • {(res.Status == UpdateCheckStatus.UpToDate ? "Up to date" : "Check Updates")}";
            var dlg = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
                Title = res.Status == UpdateCheckStatus.UpToDate ? "FluentNet is Up to Date" : "Version Check",
                Content = res.StatusMessage,
                SecondaryButtonText = "Open GitHub Releases",
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Secondary)
            {
                AppLinks.OpenInBrowser(res.ReleasePageUrl);
            }
        }
    }

    private async void DownloadOrOpenRelease_Click(object sender, RoutedEventArgs e)
    {
        if (_lastUpdateCheck is { Status: UpdateCheckStatus.UpdateAvailable } res)
        {
            await DownloadOrOpenReleaseAsync(res);
        }
        else
        {
            AppLinks.OpenInBrowser(AppLinks.ReleasesPage);
        }
    }

    private async Task DownloadOrOpenReleaseAsync(UpdateCheckResult res)
    {
        if (string.IsNullOrWhiteSpace(res.AssetDownloadUrl))
        {
            AppLinks.OpenInBrowser(res.ReleasePageUrl);
            return;
        }

        DownloadUpdateButton.IsEnabled = false;
        StatusBanner.Severity = InfoBarSeverity.Informational;
        StatusBanner.Title = $"Downloading FluentNet v{res.LatestVersion}...";
        StatusBanner.Message = res.AssetDownloadUrl;
        StatusBanner.IsOpen = true;

        try
        {
            UpdateDownloadResult dl = await _updater.DownloadReleasePackageAsync(res);
            StatusBanner.Severity = dl.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            StatusBanner.Title = dl.Succeeded ? "Update package ready" : "Update download failed";
            StatusBanner.Message = dl.Message;
            StatusBanner.IsOpen = true;
            if (dl.Succeeded && !string.IsNullOrWhiteSpace(dl.LocalFilePath))
            {
                AppLinks.OpenFolder(Path.GetDirectoryName(dl.LocalFilePath)!);
            }
        }
        finally
        {
            DownloadUpdateButton.IsEnabled = true;
        }
    }

    private void OpenGitHubRepo_Click(object sender, RoutedEventArgs e) => AppLinks.OpenInBrowser(AppLinks.GitHubRepo);
    private void OpenDeveloperProfile_Click(object sender, RoutedEventArgs e) => AppLinks.OpenInBrowser(AppLinks.DeveloperProfile);
    private void OpenGitHubIssues_Click(object sender, RoutedEventArgs e) => AppLinks.OpenInBrowser(AppLinks.IssuesPage);
}
