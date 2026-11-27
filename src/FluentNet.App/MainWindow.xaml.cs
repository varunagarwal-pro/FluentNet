using System.Reflection;
using System.Text.Json;
using FluentNet.Core.Models;
using FluentNet.Core.Serialization;
using FluentNet.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace FluentNet.App;

public sealed record AdapterDisplayRow(
    string Alias,
    string Description,
    string NetworkMeta,
    string SpeedAndMtu,
    string IpAddress,
    string StatusLabel,
    Brush StatusBg,
    Brush StatusFg,
    string IconPath);

public sealed record StackChipRow(
    string Label,
    string Subtitle,
    string ValueText,
    Brush DotBrush,
    Brush BadgeBg,
    Brush BadgeFg);

public sealed record DriverPropRow(
    string DisplayName,
    string RegistryKeyword,
    string DisplayValue);

public sealed record SnapshotDisplayRow(
    string FilePath,
    string SnapshotId,
    string ProfileName,
    string ChangeCountLabel,
    string OsBuildLabel,
    string TimestampLocal);

public sealed partial class MainWindow : Window
{
    private readonly HostScanner _scanner = new();
    private readonly ProfileEngine _profileEngine = new();
    private readonly DiffEngine _diffEngine = new();
    private readonly ApplyEngine _applyEngine = new();
    private readonly SnapshotManager _snapshotManager = new();
    private readonly IcmpDiagnosticsService _icmp = new();
    private readonly UpdateService _updater = new();

    private HostScanReport? _lastReport;
    private UpdateCheckResult? _lastUpdateCheck;
    private Dictionary<string, string> _customOverrides = new(StringComparer.OrdinalIgnoreCase);
    private ProfileKind _activeProfile = ProfileKind.RecommendedOptimal;
    private CancellationTokenSource? _diagCts;
    private bool _syncingControls;
    private string _searchQuery = string.Empty;
    private readonly string _appVersion;

    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
    private static readonly Brush AmberBrush = new SolidColorBrush(Color.FromArgb(255, 245, 158, 11));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
    private static readonly Brush UpBgBrush = new SolidColorBrush(Color.FromArgb(36, 16, 185, 129));
    private static readonly Brush AmberBgBrush = new SolidColorBrush(Color.FromArgb(36, 245, 158, 11));
    private static readonly Brush AccentBgBrush = new SolidColorBrush(Color.FromArgb(36, 56, 189, 248));
    private static readonly Brush DownBgBrush = new SolidColorBrush(Color.FromArgb(32, 148, 163, 184));

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        InitializeDropdownSources();
        SnapshotFolderText.Text = _snapshotManager.SnapshotDirectory;
        _appVersion = UpdateService.GetInstalledVersion(Assembly.GetExecutingAssembly());
        AppTitleBar.Subtitle = $"64-Bit Native Network Optimizer • v{_appVersion}";
        UpdateNavTitle.Text = $"v{_appVersion} • Check Updates";
        AboutVersionText.Text = $"FluentNet v{_appVersion} • 64-bit Windows Network Stack Optimizer • MIT Open Source • Zero Telemetry";

        bool isElevated = ElevationLauncher.IsProcessElevated();
        PrivilegePillText.Text = isElevated ? "Administrator (Direct .NET Apply)" : "Standard Mode (On-Demand UAC)";
        PrivilegePillText.Foreground = isElevated ? GreenBrush : MutedBrush;

        _ = RunHostScanAsync();
        _ = CheckForUpdatesBackgroundAsync();
    }

    private void InitializeDropdownSources()
    {
        _syncingControls = true;
        CmbAutoTuning.ItemsSource = new[] { "Normal", "Disabled", "HighlyRestricted", "Restricted", "Experimental" };
        CmbHeuristics.ItemsSource = new[] { "Disabled", "Enabled" };
        CmbCongestion.ItemsSource = new[] { "CUBIC", "CTCP", "BBR2", "Default" };
        CmbEcn.ItemsSource = new[] { "Disabled", "Enabled", "Default" };
        CmbRss.ItemsSource = new[] { "Enabled", "Disabled" };
        CmbRsc.ItemsSource = new[] { "Enabled", "Disabled" };
        CmbFastOpen.ItemsSource = new[] { "Enabled", "Disabled" };
        CmbTimestamps.ItemsSource = new[] { "Disabled", "Enabled", "Default" };
        CmbTtl.ItemsSource = new[] { "64", "128", "255" };
        CmbLoopbackMtu.ItemsSource = new[] { "Enabled", "Disabled" };

        CmbThrottling.ItemsSource = new[] { "0xffffffff", "0x0000000a", "0x0000001e" };
        CmbResponsiveness.ItemsSource = new[] { "0", "10", "20" };
        CmbAckFreq.ItemsSource = new[] { "NotSet", "1", "2" };
        CmbNoDelay.ItemsSource = new[] { "NotSet", "0", "1" };
        CmbDelAckTicks.ItemsSource = new[] { "NotSet", "0", "1", "2" };
        CmbAfdFastSend.ItemsSource = new[] { "1500", "1024", "NotSet" };

        CmbMaxSyn.ItemsSource = new[] { "2", "3", "4" };
        CmbNonSack.ItemsSource = new[] { "Disabled", "Enabled" };
        CmbInitialRto.ItemsSource = new[] { "1000", "2000", "3000" };
        CmbMinRto.ItemsSource = new[] { "300", "200", "100" };
        CmbPacing.ItemsSource = new[] { "Off", "InitialWindow", "SlowStart", "Always" };
        CmbHyStart.ItemsSource = new[] { "Enabled", "Disabled" };

        CmbMaxUserPort.ItemsSource = new[] { "65534", "NotSet" };
        CmbTimedWait.ItemsSource = new[] { "30", "60", "120", "NotSet" };
        CmbQosLimit.ItemsSource = new[] { "0", "20", "NotSet" };
        CmbQosNla.ItemsSource = new[] { "1", "0", "NotSet" };

        BdpUnitCombo.ItemsSource = new[] { "Mbps", "Gbps" };
        BdpUnitCombo.SelectedIndex = 0;

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

    private async void AccelRescan_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunHostScanAsync();
    }

    private void AccelClearSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!string.IsNullOrEmpty(TitleSearchBox.Text))
        {
            TitleSearchBox.Text = string.Empty;
            args.Handled = true;
        }
    }

    private void AccelNav1_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { NavView.SelectedItem = NavDashboard; args.Handled = true; }
    private void AccelNav2_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { NavView.SelectedItem = NavGeneral; args.Handled = true; }
    private void AccelNav3_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { NavView.SelectedItem = NavAdvanced; args.Handled = true; }
    private void AccelNav4_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { NavView.SelectedItem = NavDiagnostics; args.Handled = true; }
    private void AccelNav5_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { NavView.SelectedItem = NavSnapshots; args.Handled = true; }

    private void TitleSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _searchQuery = sender.Text.Trim();
        RefreshAdaptersView();
        RefreshDriverProperties();
    }

    private async void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
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
            await RefreshSnapshotsAsync();
        }
    }

    private async Task RunHostScanAsync()
    {
        ParamsCountPillText.Text = "Scanning stack...";
        StatusBanner.IsOpen = false;

        try
        {
            _lastReport = await _scanner.ScanAsync();
            OsEnvironmentInfo os = _lastReport.OsInfo;

            KpiOsText.Text = $"{os.ProductName} {os.DisplayVersion}";
            KpiCpuText.Text = $"Build {os.BuildNumber} • {os.PhysicalCoreCount}C / {os.LogicalProcessorCount}T";

            NetworkAdapterInfo? primary = _lastReport.Adapters.FirstOrDefault(a => a.IsUp);
            KpiNicText.Text = primary?.Alias ?? "None Active";
            KpiLinkText.Text = primary is not null
                ? $"{primary.LinkSpeedMbps} Mbps • MTU {primary.Ipv4Mtu}"
                : "No active link";

            string cong = _lastReport.Parameters.FirstOrDefault(p => p.Id == "tcp.congestion")?.CurrentValue ?? "CUBIC";
            string tfo = _lastReport.Parameters.FirstOrDefault(p => p.Id == "tcp.fastopen")?.CurrentValue ?? "—";
            KpiCongestionText.Text = cong;
            KpiBbrGuardText.Text = os.RequiresBbr2LoopbackGuard
                ? $"24H2+ Guard Ready • TFO: {tfo}"
                : $"TFO: {tfo}";

            ParamsCountPillText.Text = $"{_lastReport.Parameters.Count} Live Parameters";

            RefreshStackChips();
            RefreshAdaptersView();
            ApplyProfileToControls(_activeProfile);
        }
        catch (Exception ex)
        {
            StatusBanner.Severity = InfoBarSeverity.Error;
            StatusBanner.Title = "Host scan failed";
            StatusBanner.Message = ex.Message;
            StatusBanner.IsOpen = true;
        }
    }

    private void RefreshStackChips()
    {
        if (_lastReport is null) return;

        string Val(string id, string fb = "—") =>
            _lastReport.Parameters.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.CurrentValue ?? fb;

        string autoTuning = Val("tcp.autotuning", "Normal");
        string rss = Val("offload.rss", "Enabled");
        string rsc = Val("offload.rsc", "Enabled");
        string tfo = Val("tcp.fastopen", "Enabled");
        string ecn = Val("tcp.ecn", "Disabled");
        string ts = Val("tcp.timestamps", "Disabled");
        string loopback = Val("tcp.loopback_large_mtu", "Enabled");
        string throttle = Val("reg.network_throttling", "NotSet");

        bool throttleOff = throttle.Equals("0xffffffff", StringComparison.OrdinalIgnoreCase);

        var chips = new List<StackChipRow>
        {
            MakeChip("TCP Auto-Tuning", "Receive window scaling", autoTuning, autoTuning.Equals("Normal", StringComparison.OrdinalIgnoreCase)),
            MakeChip("RSS / RSC Offloads", "Multi-core & coalescing", $"{rss} / {rsc}", rss.Equals("Enabled", StringComparison.OrdinalIgnoreCase)),
            MakeChip("TCP Fast Open", "0-RTT SYN handshake", tfo, tfo.Equals("Enabled", StringComparison.OrdinalIgnoreCase)),
            MakeChip("ECN & Timestamps", "Header overhead & signaling", $"{ecn} / {ts}", ts.Equals("Disabled", StringComparison.OrdinalIgnoreCase)),
            MakeChip("Loopback Large MTU", "Win11 24H2+ BBR2 guard", loopback, true),
            MakeChip("Multimedia Throttling", "NetworkThrottlingIndex", throttleOff ? "Disabled (Max)" : throttle, throttleOff)
        };

        StackChipsRepeater.ItemsSource = chips;
    }

    private static StackChipRow MakeChip(string label, string sub, string value, bool healthy) =>
        new(
            Label: label,
            Subtitle: sub,
            ValueText: value,
            DotBrush: healthy ? GreenBrush : AmberBrush,
            BadgeBg: healthy ? UpBgBrush : AmberBgBrush,
            BadgeFg: healthy ? GreenBrush : AmberBrush);

    private IReadOnlyList<NetworkAdapterInfo> GetVisibleAdapters()
    {
        if (_lastReport is null) return Array.Empty<NetworkAdapterInfo>();
        if (ToggleVirtualAdapters.IsOn) return _lastReport.Adapters;

        return _lastReport.Adapters
            .Where(a => !HostDiscoveryService.IsGhostSecondaryAdapter(a))
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
                a.Description.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                (a.Ipv4Address?.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        List<AdapterDisplayRow> rows = query.Select(a =>
        {
            string iconPath = a.MediaKind switch
            {
                AdapterMediaKind.WiFi => "Assets/icons/wifi.png",
                AdapterMediaKind.Ethernet => "Assets/icons/ethernet.png",
                _ => "Assets/icons/adapter_other.png"
            };

            var metaParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(a.GatewayAddress)) metaParts.Add($"GW {a.GatewayAddress}");
            if (a.DnsServers is { Count: > 0 }) metaParts.Add($"DNS {string.Join(", ", a.DnsServers.Take(2))}");
            if (!string.IsNullOrWhiteSpace(a.MacAddress)) metaParts.Add($"MAC {a.MacAddress}");
            string meta = metaParts.Count > 0 ? string.Join("  •  ", metaParts) : "No gateway or DNS bound";

            return new AdapterDisplayRow(
                Alias: a.Alias,
                Description: a.Description,
                NetworkMeta: meta,
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

        DriverPropsHeader.Text = $"Selected Adapter Driver Properties ({nic.Alias} • {nic.AdvancedProperties.Count} properties)";
        IEnumerable<AdapterAdvancedPropertyInfo> props = nic.AdvancedProperties;
        if (!string.IsNullOrEmpty(_searchQuery))
        {
            props = props.Where(p =>
                p.DisplayName.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                p.RegistryKeyword.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                p.DisplayValue.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));
        }

        DriverPropsRepeater.ItemsSource = props
            .Select(p => new DriverPropRow(p.DisplayName, p.RegistryKeyword, p.DisplayValue))
            .ToList();
    }

    private void SetActiveProfilePill(ProfileKind kind)
    {
        _activeProfile = kind;
        _syncingControls = true;
        BtnProfileDefault.IsChecked = kind == ProfileKind.WindowsDefault;
        BtnProfileCurrent.IsChecked = kind == ProfileKind.Current;
        BtnProfileOptimal.IsChecked = kind == ProfileKind.RecommendedOptimal;
        BtnProfileGaming.IsChecked = kind == ProfileKind.GamingLowLatency;
        BtnProfileCustom.IsChecked = kind == ProfileKind.Custom;
        _syncingControls = false;
    }

    private void ProfilePill_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingControls || sender is not ToggleButton btn || btn.Tag is not string tag) return;

        ProfileKind kind = tag switch
        {
            "default" => ProfileKind.WindowsDefault,
            "current" => ProfileKind.Current,
            "gaming" => ProfileKind.GamingLowLatency,
            "custom" => ProfileKind.Custom,
            _ => ProfileKind.RecommendedOptimal
        };

        SetActiveProfilePill(kind);
        if (_lastReport is not null)
        {
            ApplyProfileToControls(kind);
        }
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
        SelectItem(CmbEcn, GetTarget("tcp.ecn", "Disabled"));
        SelectItem(CmbRss, GetTarget("offload.rss", "Enabled"));
        SelectItem(CmbRsc, GetTarget("offload.rsc", "Enabled"));
        SelectItem(CmbFastOpen, GetTarget("tcp.fastopen", "Enabled"));
        SelectItem(CmbTimestamps, GetTarget("tcp.timestamps", "Disabled"));
        SelectItem(CmbTtl, GetTarget("reg.default_ttl", "64"));
        SelectItem(CmbLoopbackMtu, GetTarget("tcp.loopback_large_mtu", "Enabled"));

        SelectItem(CmbThrottling, GetTarget("reg.network_throttling", "0xffffffff"));
        SelectItem(CmbResponsiveness, GetTarget("reg.system_responsiveness", "10"));
        SelectItem(CmbAfdFastSend, GetTarget("reg.afd_fast_send", "1500"));

        SelectItem(CmbMaxSyn, GetTarget("tcp.maxsynretx", "2"));
        SelectItem(CmbNonSack, GetTarget("tcp.nonsackrtt", "Disabled"));
        SelectItem(CmbInitialRto, GetTarget("tcp.initialrto", "2000"));
        SelectItem(CmbMinRto, GetTarget("tcp.minrto", "300"));
        SelectItem(CmbPacing, GetTarget("tcp.pacing", "Off"));
        SelectItem(CmbHyStart, GetTarget("tcp.hystart", "Enabled"));

        SelectItem(CmbMaxUserPort, GetTarget("reg.max_user_port", "65534"));
        SelectItem(CmbTimedWait, GetTarget("reg.tcp_timed_wait_delay", "30"));
        SelectItem(CmbQosLimit, GetTarget("reg.qos_non_best_effort", "0"));
        SelectItem(CmbQosNla, GetTarget("reg.qos_do_not_use_nla", "1"));

        if (nic is not null)
        {
            SelectItem(CmbAckFreq, GetTarget($"nic.{nic.InterfaceGuid}.tcpackfreq", "NotSet"));
            SelectItem(CmbNoDelay, GetTarget($"nic.{nic.InterfaceGuid}.tcpnodelay", "NotSet"));
            SelectItem(CmbDelAckTicks, GetTarget($"nic.{nic.InterfaceGuid}.tcpdelackticks", "NotSet"));
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

        if (diffCount == 0)
        {
            KpiDriftText.Text = "All Aligned";
            KpiDriftIcon.Foreground = GreenBrush;
            KpiDriftIconBox.Background = UpBgBrush;
            KpiDriftHeader.Foreground = GreenBrush;
            ApplyChangesButton.Content = "All Settings Aligned";
            ApplyChangesButton.IsEnabled = false;
        }
        else
        {
            string word = diffCount == 1 ? "Change" : "Changes";
            KpiDriftText.Text = $"{diffCount} {word}";
            KpiDriftIcon.Foreground = AccentBrush;
            KpiDriftIconBox.Background = AccentBgBrush;
            KpiDriftHeader.Foreground = AccentBrush;
            ApplyChangesButton.Content = $"Review & Apply ({diffCount})";
            ApplyChangesButton.IsEnabled = true;
        }

        KpiProfileLabel.Text = $"Profile: {friendly}";
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

    private NetworkAdapterInfo? GetSelectedAdapter()
    {
        IReadOnlyList<NetworkAdapterInfo> visible = GetVisibleAdapters();
        if (AdapterCombo.SelectedIndex >= 0 && AdapterCombo.SelectedIndex < visible.Count)
        {
            return visible[AdapterCombo.SelectedIndex];
        }
        return visible.FirstOrDefault(a => a.IsUp) ?? _lastReport?.Adapters.FirstOrDefault(a => a.IsUp);
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        SetActiveProfilePill(ProfileKind.WindowsDefault);
        ApplyProfileToControls(ProfileKind.WindowsDefault);
    }

    private void InteractiveCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || sender is not ComboBox box || box.Tag is not string key || box.SelectedItem is not string val) return;

        _customOverrides[key] = val;
        if (key == "tcp.congestion" && _lastReport is not null)
        {
            string loopbackTarget = string.Equals(val, "BBR2", StringComparison.OrdinalIgnoreCase) && _lastReport.OsInfo.RequiresBbr2LoopbackGuard
                ? "Disabled"
                : "Enabled";
            _customOverrides["tcp.loopback_large_mtu"] = loopbackTarget;
            _syncingControls = true;
            SelectItem(CmbLoopbackMtu, loopbackTarget);
            _syncingControls = false;
        }

        SetActiveProfilePill(ProfileKind.Custom);
        UpdatePendingSummary(ProfileKind.Custom);
    }

    private void InteractiveNicCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || sender is not ComboBox box || box.Tag is not string suffix || box.SelectedItem is not string val) return;
        NetworkAdapterInfo? nic = GetSelectedAdapter();
        if (nic is null) return;

        string key = suffix.Replace("nic.", $"nic.{nic.InterfaceGuid}.");
        _customOverrides[key] = val;

        SetActiveProfilePill(ProfileKind.Custom);
        UpdatePendingSummary(ProfileKind.Custom);
    }

    private void SpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (SpeedLabelText is null) return;
        int mbps = (int)e.NewValue;
        int mtu = int.TryParse(MtuBox?.Text, out int parsedMtu) && parsedMtu >= 576 ? parsedMtu : 1500;
        BdpResult bdp = BdpCalculator.Calculate(mbps, BandwidthUnit.Mbps, 40, mtu);
        string speedStr = mbps >= 1000 ? $"{mbps / 1000.0:0.#} Gbps" : $"{mbps} Mbps";
        SpeedLabelText.Text = $"{speedStr}  •  RWIN {bdp.SuggestedRwinBytes / 1024.0:N0} KB @ 40ms (Scale {bdp.RecommendedScaleFactor})";
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
        ApplyProfileToControls(_activeProfile);
    }

    private void MtuBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingControls) return;
        NetworkAdapterInfo? nic = GetSelectedAdapter();
        if (nic is null) return;
        if (int.TryParse(MtuBox.Text.Trim(), out int mtu) && mtu >= 576 && mtu <= 9000)
        {
            _customOverrides[$"nic.{nic.InterfaceGuid}.mtu"] = mtu.ToString();
            SetActiveProfilePill(ProfileKind.Custom);
            UpdatePendingSummary(ProfileKind.Custom);
        }
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
            StatusBanner.Title = "All settings aligned";
            StatusBanner.Message = "Current host settings already match your selected profile.";
            StatusBanner.IsOpen = true;
            return;
        }

        string friendly = KpiProfileLabel.Text.Replace("Profile: ", string.Empty);
        var dialog = new ReviewChangesDialog(friendly, diffs)
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            IReadOnlyList<ChangePlanItem> selected = dialog.GetSelectedItems();
            if (selected.Count > 0)
            {
                await ApplyChangesNativeOrElevatedAsync(selected, friendly);
            }
        }
    }

    private async void ExportScripts_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null) return;
        IReadOnlyList<ChangePlanItem> diffs = _diffEngine.ComputeDiff(_lastReport, _customOverrides);
        string profileName = _activeProfile.ToString();
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

    private async Task ApplyChangesNativeOrElevatedAsync(IReadOnlyList<ChangePlanItem> diffs, string profileName)
    {
        if (_lastReport is null) return;

        var manifest = new SnapshotManifest(
            $"snap-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
            DateTimeOffset.UtcNow,
            _lastReport.OsInfo.BuildNumber.ToString(),
            profileName,
            diffs);

        ApplyReport? report;

        // Fast path: if already elevated, apply 100% in-process via native .NET CIM & Registry (zero child process!)
        if (ElevationLauncher.IsProcessElevated())
        {
            string snapPath = await _snapshotManager.SaveSnapshotAsync(manifest);
            report = await _applyEngine.ApplyPlanAsync(manifest.Items);
            report = report with { SnapshotPath = snapPath };
        }
        else
        {
            string? cli = ElevationLauncher.ResolveCliPath();
            if (cli is null)
            {
                StatusBanner.Severity = InfoBarSeverity.Warning;
                StatusBanner.Title = "Elevated worker not found";
                StatusBanner.Message = "Place FluentNet.Cli.exe beside FluentNet.exe, run FluentNet as Administrator, or export .ps1/.reg scripts.";
                StatusBanner.IsOpen = true;
                return;
            }

            string tempPlan = Path.Combine(Path.GetTempPath(), $"fluentnet-plan-{Guid.NewGuid():N}.json");
            string tempResult = Path.Combine(Path.GetTempPath(), $"fluentnet-result-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(tempPlan, JsonSerializer.Serialize(manifest, FluentNetJsonContext.Default.SnapshotManifest));

            try
            {
                int code = await Task.Run(() =>
                    ElevationLauncher.RunElevatedCli(cli, $"apply --plan \"{tempPlan}\" --result \"{tempResult}\"", hideWindow: true));

                if (File.Exists(tempResult))
                {
                    string resJson = await File.ReadAllTextAsync(tempResult);
                    report = JsonSerializer.Deserialize(resJson, FluentNetJsonContext.Default.ApplyReport);
                }
                else
                {
                    report = new ApplyReport(code == 0, Array.Empty<ApplyItemResult>());
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                StatusBanner.Severity = InfoBarSeverity.Informational;
                StatusBanner.Title = "UAC prompt cancelled";
                StatusBanner.Message = "No changes were made to your system.";
                StatusBanner.IsOpen = true;
                return;
            }
            finally
            {
                TryDeleteFile(tempPlan);
                TryDeleteFile(tempResult);
            }
        }

        await RunHostScanAsync();

        if (report is not null && report.AllSucceeded)
        {
            StatusBanner.Severity = InfoBarSeverity.Success;
            StatusBanner.Title = $"Applied {diffs.Count} setting(s) natively via .NET";
            StatusBanner.Message = "Pre-change JSON snapshot saved. Live host state has been re-verified.";
        }
        else
        {
            List<ApplyItemResult> failed = report?.Results.Where(r => !r.Succeeded).ToList() ?? new List<ApplyItemResult>();
            string detail = failed.Count > 0
                ? string.Join(" | ", failed.Take(3).Select(f => $"{f.ParameterId}: {f.Message}"))
                : "One or more settings could not be applied.";
            StatusBanner.Severity = InfoBarSeverity.Error;
            StatusBanner.Title = failed.Count > 0 ? $"{failed.Count} setting(s) failed to apply" : "Apply failed";
            StatusBanner.Message = detail;
        }
        StatusBanner.IsOpen = true;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore cleanup */ }
    }

    private void CalculateBdp_Click(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(BdpBandwidthBox.Text, out double bw) &&
            double.TryParse(BdpLatencyBox.Text, out double ms) &&
            int.TryParse(BdpMtuBox.Text, out int mtu))
        {
            BandwidthUnit unit = BdpUnitCombo.SelectedIndex == 1 ? BandwidthUnit.Gbps : BandwidthUnit.Mbps;
            BdpResult r = BdpCalculator.Calculate(bw, unit, ms, mtu);
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
        DiagSpinner.IsActive = true;
        DiagSpinner.Visibility = Visibility.Visible;
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
            DiagSpinner.IsActive = false;
            DiagSpinner.Visibility = Visibility.Collapsed;
        }
    }

    private void StopDiag_Click(object sender, RoutedEventArgs e) => _diagCts?.Cancel();
    private void ClearDiag_Click(object sender, RoutedEventArgs e) => DiagOutputBox.Text = string.Empty;

    private void CopyDiag_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DiagOutputBox.Text)) return;
        var pkg = new DataPackage();
        pkg.SetText(DiagOutputBox.Text);
        Clipboard.SetContent(pkg);
        StatusBanner.Severity = InfoBarSeverity.Success;
        StatusBanner.Title = "Copied";
        StatusBanner.Message = "Diagnostic output copied to clipboard.";
        StatusBanner.IsOpen = true;
    }

    private async Task RefreshSnapshotsAsync()
    {
        IReadOnlyList<SnapshotSummary> summaries = await _snapshotManager.ListSnapshotSummariesAsync();
        List<SnapshotDisplayRow> rows = summaries.Select(s => new SnapshotDisplayRow(
            FilePath: s.FilePath,
            SnapshotId: s.SnapshotId,
            ProfileName: s.ProfileName,
            ChangeCountLabel: s.ChangeCount == 1 ? "1 setting" : $"{s.ChangeCount} settings",
            OsBuildLabel: $"Build {s.OsBuild}",
            TimestampLocal: s.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))).ToList();

        SnapshotsList.ItemsSource = rows;
        SnapshotsEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshSnapshots_Click(object sender, RoutedEventArgs e) => await RefreshSnapshotsAsync();

    private async void DeleteSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsList.SelectedItem is not SnapshotDisplayRow row) return;
        _snapshotManager.DeleteSnapshot(row.FilePath);
        await RefreshSnapshotsAsync();
    }

    private async void RollbackSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsList.SelectedItem is not SnapshotDisplayRow row) return;

        if (ElevationLauncher.IsProcessElevated())
        {
            SnapshotManifest? snap = await _snapshotManager.LoadSnapshotAsync(row.FilePath);
            if (snap is null || snap.Items.Count == 0) return;
            List<ChangePlanItem> revert = snap.Items.Select(i => i with { OldValue = i.NewValue, NewValue = i.OldValue }).ToList();
            ApplyReport res = await _applyEngine.ApplyPlanAsync(revert);
            await RunHostScanAsync();
            StatusBanner.Severity = res.AllSucceeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            StatusBanner.Title = res.AllSucceeded ? "Rollback complete (native .NET)" : "Rollback completed with errors";
            StatusBanner.Message = res.AllSucceeded
                ? $"Restored {revert.Count} setting(s) from {row.SnapshotId}."
                : string.Join(" | ", res.Results.Where(r => !r.Succeeded).Take(3).Select(r => $"{r.ParameterId}: {r.Message}"));
            StatusBanner.IsOpen = true;
            return;
        }

        string? cli = ElevationLauncher.ResolveCliPath();
        if (cli is null) return;
        int code = await Task.Run(() => ElevationLauncher.RunElevatedCli(cli, $"rollback --snapshot \"{row.FilePath}\"", hideWindow: true));
        await RunHostScanAsync();
        StatusBanner.Severity = code == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        StatusBanner.Title = code == 0 ? "Rollback complete" : $"Rollback exited with code {code}";
        StatusBanner.Message = code == 0 ? $"Restored settings from {row.SnapshotId}." : "One or more settings failed to roll back.";
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

            if (!showDialogOnNew || Content?.XamlRoot is null) return;

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
