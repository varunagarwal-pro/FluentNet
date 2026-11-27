using System.ComponentModel;
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
using Windows.Foundation;
using Windows.UI;

namespace FluentNet.App;

public sealed record SettingChoice(string Label, string Value)
{
    public override string ToString() => Label;
}

public sealed record AdapterDisplayRow(
    string Alias,
    string Description,
    string NetworkMeta,
    string SpeedAndMtu,
    string IpAddress,
    string StatusLabel,
    Brush StatusBg,
    Brush StatusBorder,
    Brush StatusFg,
    string IconPath);

public sealed record StackChipRow(
    string Label,
    string Subtitle,
    string ValueText,
    Brush DotBrush,
    Brush BadgeBg,
    Brush BadgeBorder,
    Brush BadgeFg);

public sealed class DriverPropRow : INotifyPropertyChanged
{
    private string _selectedValue;

    public DriverPropRow(string displayName, string registryKeyword, string displayValue, IReadOnlyList<string>? validValues)
    {
        DisplayName = displayName;
        RegistryKeyword = registryKeyword;
        DisplayValue = displayValue;
        ValidValues = validValues is { Count: > 1 } ? validValues : Array.Empty<string>();
        _selectedValue = displayValue;
    }

    public string DisplayName { get; }
    public string RegistryKeyword { get; }
    public string DisplayValue { get; }
    public IReadOnlyList<string> ValidValues { get; }
    public Visibility ComboVisibility => ValidValues.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgeVisibility => ValidValues.Count > 1 ? Visibility.Collapsed : Visibility.Visible;

    public string SelectedValue
    {
        get => _selectedValue;
        set
        {
            if (!string.Equals(_selectedValue, value, StringComparison.Ordinal))
            {
                _selectedValue = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedValue)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class SnapshotDisplayRow : INotifyPropertyChanged
{
    private static readonly Brush SelectedBg = new SolidColorBrush(Color.FromArgb(30, 56, 189, 248));
    private static readonly Brush NormalBg = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255));
    private static readonly Brush SelectedBorder = new SolidColorBrush(Color.FromArgb(220, 56, 189, 248));
    private static readonly Brush NormalBorder = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255));

    private bool _isSelected;

    public SnapshotDisplayRow(string filePath, string snapshotId, string profileName, string changeCountLabel, string osBuildLabel, string timestampLocal)
    {
        FilePath = filePath;
        SnapshotId = snapshotId;
        ProfileName = profileName;
        ChangeCountLabel = changeCountLabel;
        OsBuildLabel = osBuildLabel;
        TimestampLocal = timestampLocal;
    }

    public string FilePath { get; }
    public string SnapshotId { get; }
    public string ProfileName { get; }
    public string ChangeCountLabel { get; }
    public string OsBuildLabel { get; }
    public string TimestampLocal { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBackground)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBorderBrush)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBorderThickness)));
            }
        }
    }

    public Brush CardBackground => _isSelected ? SelectedBg : NormalBg;
    public Brush CardBorderBrush => _isSelected ? SelectedBorder : NormalBorder;
    public Thickness CardBorderThickness => _isSelected ? new Thickness(2) : new Thickness(1);

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class MainWindow : Window
{
    private readonly HostScanner _scanner = new();
    private readonly ProfileEngine _profileEngine = new();
    private readonly DiffEngine _diffEngine = new();
    private readonly ApplyEngine _applyEngine = new();
    private readonly SnapshotManager _snapshotManager = new();
    private readonly IcmpDiagnosticsService _icmp = new();
    private readonly DnsOptimizerService _dns = new();
    private readonly UpdateService _updater = new();

    private HostScanReport? _lastReport;
    private UpdateCheckResult? _lastUpdateCheck;
    private DnsBenchmarkResult? _fastestDns;
    private List<SnapshotDisplayRow> _snapshotRows = new();
    private Dictionary<string, string> _customOverrides = new(StringComparer.OrdinalIgnoreCase);
    private ProfileKind _activeProfile = ProfileKind.RecommendedOptimal;
    private CancellationTokenSource? _diagCts;
    private bool _syncingControls;
    private string _searchQuery = string.Empty;
    private readonly string _appVersion;

    // Live RX/TX throughput + 30-sample sparkline
    private readonly DispatcherTimer _throughputTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Queue<double> _sparklineSamples = new();
    private string? _activeAdapterGuid;
    private long _lastRxBytes;
    private long _lastTxBytes;
    private DateTimeOffset _lastSampleAt;

    // Option 1A: Floating 5-second auto-dismiss toast timer
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private double _toastRemainingMs;
    private bool _toastPaused;
    private bool _toastAutoDismiss = true;
    private const double ToastDurationMs = 5000.0;

    // Modified setting card lookup map
    private readonly Dictionary<string, Border> _settingCardsByKey = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
    private static readonly Brush AmberBrush = new SolidColorBrush(Color.FromArgb(255, 245, 158, 11));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromArgb(255, 248, 113, 113));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
    private static readonly Brush UpBgBrush = new SolidColorBrush(Color.FromArgb(28, 16, 185, 129));
    private static readonly Brush UpBorderBrush = new SolidColorBrush(Color.FromArgb(72, 16, 185, 129));
    private static readonly Brush AmberBgBrush = new SolidColorBrush(Color.FromArgb(30, 245, 158, 11));
    private static readonly Brush AmberBorderBrush = new SolidColorBrush(Color.FromArgb(72, 245, 158, 11));
    private static readonly Brush RedBgBrush = new SolidColorBrush(Color.FromArgb(30, 239, 68, 68));
    private static readonly Brush RedBorderBrush = new SolidColorBrush(Color.FromArgb(80, 239, 68, 68));
    private static readonly Brush AccentBgBrush = new SolidColorBrush(Color.FromArgb(28, 56, 189, 248));
    private static readonly Brush AccentBorderBrush = new SolidColorBrush(Color.FromArgb(95, 56, 189, 248));
    private static readonly Brush DownBgBrush = new SolidColorBrush(Color.FromArgb(24, 148, 163, 184));
    private static readonly Brush DownBorderBrush = new SolidColorBrush(Color.FromArgb(64, 148, 163, 184));
    private static readonly Brush SubtleBorderBrush = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        RegisterSettingCardMap();
        InitializeDropdownSources();
        SnapshotFolderText.Text = _snapshotManager.SnapshotDirectory;
        _appVersion = UpdateService.GetInstalledVersion(Assembly.GetExecutingAssembly());
        AppTitleBar.Subtitle = $"Windows Network Optimizer • v{_appVersion}";
        UpdateNavTitle.Text = $"v{_appVersion} • Check Updates";
        AboutVersionText.Text = $"FluentNet v{_appVersion} • 64-bit Windows Network Optimizer • MIT Open Source • Zero Telemetry";

        bool isElevated = ElevationLauncher.IsProcessElevated();
        PrivilegeTagText.Text = isElevated ? "ADMIN" : "STANDARD";
        PrivilegeTagText.Foreground = isElevated ? GreenBrush : MutedBrush;
        PrivilegeTag.Background = isElevated ? UpBgBrush : DownBgBrush;
        PrivilegeTag.BorderBrush = isElevated ? UpBorderBrush : DownBorderBrush;

        _throughputTimer.Tick += ThroughputTimer_Tick;
        _toastTimer.Tick += ToastTimer_Tick;
        Closed += (_, _) =>
        {
            _throughputTimer.Stop();
            _toastTimer.Stop();
        };

        _ = RunHostScanAsync();
        _ = CheckForUpdatesBackgroundAsync();
    }

    private void RegisterSettingCardMap()
    {
        _settingCardsByKey["tcp.autotuning"] = CardAutoTuning;
        _settingCardsByKey["tcp.heuristics"] = CardHeuristics;
        _settingCardsByKey["tcp.congestion"] = CardCongestion;
        _settingCardsByKey["tcp.ecn"] = CardEcn;
        _settingCardsByKey["offload.rss"] = CardRss;
        _settingCardsByKey["offload.rsc"] = CardRsc;
        _settingCardsByKey["tcp.fastopen"] = CardFastOpen;
        _settingCardsByKey["tcp.timestamps"] = CardTimestamps;
        _settingCardsByKey["reg.default_ttl"] = CardTtl;
        _settingCardsByKey["tcp.loopback_large_mtu"] = CardLoopbackMtu;

        _settingCardsByKey["reg.network_throttling"] = CardThrottling;
        _settingCardsByKey["reg.system_responsiveness"] = CardResponsiveness;
        _settingCardsByKey["nic.tcpackfreq"] = CardAckFreq;
        _settingCardsByKey["nic.tcpnodelay"] = CardNoDelay;
        _settingCardsByKey["nic.tcpdelackticks"] = CardDelAckTicks;
        _settingCardsByKey["reg.afd_fast_send"] = CardAfdFastSend;

        _settingCardsByKey["tcp.maxsynretx"] = CardMaxSyn;
        _settingCardsByKey["tcp.nonsackrtt"] = CardNonSack;
        _settingCardsByKey["tcp.initialrto"] = CardInitialRto;
        _settingCardsByKey["tcp.minrto"] = CardMinRto;
        _settingCardsByKey["tcp.pacing"] = CardPacing;
        _settingCardsByKey["tcp.hystart"] = CardHyStart;

        _settingCardsByKey["reg.max_user_port"] = CardMaxUserPort;
        _settingCardsByKey["reg.tcp_timed_wait_delay"] = CardTimedWait;
        _settingCardsByKey["reg.qos_non_best_effort"] = CardQosLimit;
        _settingCardsByKey["reg.qos_do_not_use_nla"] = CardQosNla;
    }

    private void InitializeDropdownSources()
    {
        _syncingControls = true;

        CmbAutoTuning.ItemsSource = new[]
        {
            new SettingChoice("Normal (Recommended)", "Normal"),
            new SettingChoice("Disabled", "Disabled"),
            new SettingChoice("Highly Restricted", "HighlyRestricted"),
            new SettingChoice("Restricted", "Restricted"),
            new SettingChoice("Experimental (Max)", "Experimental")
        };
        CmbHeuristics.ItemsSource = new[]
        {
            new SettingChoice("Disabled (Recommended)", "Disabled"),
            new SettingChoice("Enabled", "Enabled")
        };
        CmbCongestion.ItemsSource = new[]
        {
            new SettingChoice("CUBIC (Standard)", "CUBIC"),
            new SettingChoice("BBR2 (Win11 Modern)", "BBR2"),
            new SettingChoice("CTCP (Compound)", "CTCP"),
            new SettingChoice("Windows Default", "Default")
        };
        CmbEcn.ItemsSource = new[]
        {
            new SettingChoice("Disabled (Recommended)", "Disabled"),
            new SettingChoice("Enabled", "Enabled"),
            new SettingChoice("Windows Default", "Default")
        };
        CmbRss.ItemsSource = new[]
        {
            new SettingChoice("Enabled (Recommended)", "Enabled"),
            new SettingChoice("Disabled", "Disabled")
        };
        CmbRsc.ItemsSource = new[]
        {
            new SettingChoice("Enabled (Throughput)", "Enabled"),
            new SettingChoice("Disabled (Low Jitter)", "Disabled")
        };
        CmbFastOpen.ItemsSource = new[]
        {
            new SettingChoice("Enabled (Recommended)", "Enabled"),
            new SettingChoice("Disabled", "Disabled")
        };
        CmbTimestamps.ItemsSource = new[]
        {
            new SettingChoice("Disabled (Recommended)", "Disabled"),
            new SettingChoice("Enabled", "Enabled"),
            new SettingChoice("Windows Default", "Default")
        };
        CmbTtl.ItemsSource = new[]
        {
            new SettingChoice("64 Hops (Optimal)", "64"),
            new SettingChoice("128 Hops (Default)", "128"),
            new SettingChoice("255 Hops (Max)", "255")
        };
        CmbLoopbackMtu.ItemsSource = new[]
        {
            new SettingChoice("Enabled (Standard)", "Enabled"),
            new SettingChoice("Disabled (BBR2 Guard)", "Disabled")
        };

        // Human-friendly Advanced Settings choices (exact technical values stored in SettingChoice.Value)
        CmbThrottling.ItemsSource = new[]
        {
            new SettingChoice("Disabled (Max Speed)", "0xffffffff"),
            new SettingChoice("Windows Default (10)", "0x0000000a"),
            new SettingChoice("Relaxed (30)", "0x0000001e")
        };
        CmbResponsiveness.ItemsSource = new[]
        {
            new SettingChoice("0% (Max Gaming)", "0"),
            new SettingChoice("10% (Balanced Optimal)", "10"),
            new SettingChoice("20% (Windows Default)", "20")
        };
        CmbAckFreq.ItemsSource = new[]
        {
            new SettingChoice("Windows Default", "NotSet"),
            new SettingChoice("Immediate ACKs (Gaming)", "1"),
            new SettingChoice("Standard (2)", "2")
        };
        CmbNoDelay.ItemsSource = new[]
        {
            new SettingChoice("Windows Default", "NotSet"),
            new SettingChoice("Instant Send (Gaming)", "1"),
            new SettingChoice("Standard Batching", "0")
        };
        CmbDelAckTicks.ItemsSource = new[]
        {
            new SettingChoice("Windows Default", "NotSet"),
            new SettingChoice("0 ms (Instant Gaming)", "0"),
            new SettingChoice("100 ms", "1"),
            new SettingChoice("200 ms (Default)", "2")
        };
        CmbAfdFastSend.ItemsSource = new[]
        {
            new SettingChoice("1500 B (Full MTU)", "1500"),
            new SettingChoice("1024 B (Default)", "1024"),
            new SettingChoice("Windows Default", "NotSet")
        };

        CmbMaxSyn.ItemsSource = new[]
        {
            new SettingChoice("2 Retries (Fast)", "2"),
            new SettingChoice("3 Retries", "3"),
            new SettingChoice("4 Retries (Default)", "4")
        };
        CmbNonSack.ItemsSource = new[]
        {
            new SettingChoice("Disabled (Recommended)", "Disabled"),
            new SettingChoice("Enabled", "Enabled")
        };
        CmbInitialRto.ItemsSource = new[]
        {
            new SettingChoice("1000 ms (Gaming)", "1000"),
            new SettingChoice("2000 ms (Optimal)", "2000"),
            new SettingChoice("3000 ms (Default)", "3000")
        };
        CmbMinRto.ItemsSource = new[]
        {
            new SettingChoice("300 ms (Safe Baseline)", "300"),
            new SettingChoice("200 ms", "200"),
            new SettingChoice("100 ms", "100")
        };
        CmbPacing.ItemsSource = new[]
        {
            new SettingChoice("Off (Default)", "Off"),
            new SettingChoice("Initial Window (Gigabit)", "InitialWindow"),
            new SettingChoice("Slow Start", "SlowStart"),
            new SettingChoice("Always", "Always")
        };
        CmbHyStart.ItemsSource = new[]
        {
            new SettingChoice("Enabled (Recommended)", "Enabled"),
            new SettingChoice("Disabled", "Disabled")
        };

        CmbMaxUserPort.ItemsSource = new[]
        {
            new SettingChoice("Expanded (65,534 Ports)", "65534"),
            new SettingChoice("Windows Default (Dynamic)", "NotSet")
        };
        CmbTimedWait.ItemsSource = new[]
        {
            new SettingChoice("30s (Fast Reuse)", "30"),
            new SettingChoice("60s (Moderate)", "60"),
            new SettingChoice("120s (Default)", "120"),
            new SettingChoice("Windows Default", "NotSet")
        };
        CmbQosLimit.ItemsSource = new[]
        {
            new SettingChoice("No Limit (0%)", "0"),
            new SettingChoice("Windows Default (20%)", "20"),
            new SettingChoice("Remove Policy", "NotSet")
        };
        CmbQosNla.ItemsSource = new[]
        {
            new SettingChoice("Enabled (Bypass NLA)", "1"),
            new SettingChoice("Disabled", "0"),
            new SettingChoice("Windows Default", "NotSet")
        };

        DnsPresetCombo.ItemsSource = new[]
        {
            new SettingChoice("Cloudflare (1.1.1.1 / 1.0.0.1)", "1.1.1.1,1.0.0.1"),
            new SettingChoice("Google Public DNS (8.8.8.8 / 8.8.4.4)", "8.8.8.8,8.8.4.4"),
            new SettingChoice("Quad9 Security (9.9.9.9 / 149.112.112.112)", "9.9.9.9,149.112.112.112"),
            new SettingChoice("AdGuard DNS (94.140.14.14 / 94.140.15.15)", "94.140.14.14,94.140.15.15"),
            new SettingChoice("OpenDNS (208.67.222.222 / 208.67.220.220)", "208.67.222.222,208.67.220.220"),
            new SettingChoice("Automatic / DHCP (Router Default)", "DHCP")
        };
        DnsPresetCombo.SelectedIndex = 0;

        BdpUnitCombo.ItemsSource = new[] { "Mbps", "Gbps" };
        BdpUnitCombo.SelectedIndex = 0;

        CmbTheme.ItemsSource = new[] { "System Default", "Dark", "Light" };
        CmbTheme.SelectedIndex = 0;

        _syncingControls = false;
    }

    // ==================== OPTION 1A: FLOATING 5-SECOND TOAST OVERLAY ====================

    private void ShowToast(InfoBarSeverity severity, string title, string message, bool autoDismiss = true)
    {
        ToastTitle.Text = title;
        ToastMessage.Text = message;

        switch (severity)
        {
            case InfoBarSeverity.Success:
                ToastIcon.Glyph = "\uE73E";
                ToastIcon.Foreground = GreenBrush;
                ToastIconBox.Background = UpBgBrush;
                ToastOverlay.BorderBrush = UpBorderBrush;
                ToastCountdownBar.Foreground = GreenBrush;
                break;
            case InfoBarSeverity.Warning:
                ToastIcon.Glyph = "\uE7BA";
                ToastIcon.Foreground = AmberBrush;
                ToastIconBox.Background = AmberBgBrush;
                ToastOverlay.BorderBrush = AmberBorderBrush;
                ToastCountdownBar.Foreground = AmberBrush;
                break;
            case InfoBarSeverity.Error:
                ToastIcon.Glyph = "\uEA39";
                ToastIcon.Foreground = RedBrush;
                ToastIconBox.Background = RedBgBrush;
                ToastOverlay.BorderBrush = RedBorderBrush;
                ToastCountdownBar.Foreground = RedBrush;
                autoDismiss = false; // Keep error notifications visible until dismissed
                break;
            default:
                ToastIcon.Glyph = "\uE946";
                ToastIcon.Foreground = AccentBrush;
                ToastIconBox.Background = AccentBgBrush;
                ToastOverlay.BorderBrush = AccentBorderBrush;
                ToastCountdownBar.Foreground = AccentBrush;
                break;
        }

        _toastAutoDismiss = autoDismiss;
        _toastRemainingMs = ToastDurationMs;
        _toastPaused = false;
        ToastCountdownBar.Visibility = autoDismiss ? Visibility.Visible : Visibility.Collapsed;
        ToastCountdownBar.Value = 100;
        ToastOverlay.Visibility = Visibility.Visible;

        if (autoDismiss)
        {
            _toastTimer.Start();
        }
        else
        {
            _toastTimer.Stop();
        }
    }

    private void ToastTimer_Tick(object? sender, object e)
    {
        if (_toastPaused || !_toastAutoDismiss) return;
        _toastRemainingMs -= 50;
        if (_toastRemainingMs <= 0)
        {
            _toastTimer.Stop();
            ToastOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        ToastCountdownBar.Value = (_toastRemainingMs / ToastDurationMs) * 100.0;
    }

    private void ToastOverlay_PointerEntered(object sender, PointerRoutedEventArgs e) => _toastPaused = true;
    private void ToastOverlay_PointerExited(object sender, PointerRoutedEventArgs e) => _toastPaused = false;

    private void DismissToast_Click(object sender, RoutedEventArgs e)
    {
        _toastTimer.Stop();
        ToastOverlay.Visibility = Visibility.Collapsed;
    }

    // ==================== NAVIGATION & SEARCH ====================

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
        FilterSettingGrids();
    }

    private void FilterSettingGrids()
    {
        FilterGridCards(GeneralCardsGrid);
        FilterGridCards(AdvGamingGrid);
        FilterGridCards(AdvRtoGrid);
        FilterGridCards(AdvQosGrid);
    }

    private void FilterGridCards(Grid grid)
    {
        foreach (UIElement child in grid.Children)
        {
            if (child is Border border)
            {
                if (string.IsNullOrEmpty(_searchQuery))
                {
                    border.Visibility = Visibility.Visible;
                }
                else
                {
                    string keywords = border.Tag?.ToString() ?? string.Empty;
                    border.Visibility = keywords.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }
        }
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

    // ==================== HOST SCAN & LIVE SPARKLINE ====================

    private async Task RunHostScanAsync()
    {
        OverviewMetaText.Text = "Scanning your live network settings...";

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

            if (primary is not null)
            {
                _activeAdapterGuid = primary.InterfaceGuid;
                (_lastRxBytes, _lastTxBytes) = HostDiscoveryService.SampleAdapterBytes(primary.InterfaceGuid);
                _lastSampleAt = DateTimeOffset.UtcNow;
                _sparklineSamples.Clear();
                _throughputTimer.Start();
            }
            else
            {
                _throughputTimer.Stop();
            }

            string cong = FindParam("tcp.congestion") ?? "CUBIC";
            string? tfo = FindParam("tcp.fastopen");
            KpiCongestionText.Text = cong;
            KpiBbrGuardText.Text = os.RequiresBbr2LoopbackGuard
                ? (tfo is not null ? $"24H2+ Guard Ready • TFO: {tfo}" : "24H2+ BBR2 Guard Ready")
                : (tfo is not null ? $"TCP Fast Open: {tfo}" : "Standard Windows Stack");

            OverviewMetaText.Text = $"{_lastReport.Parameters.Count} live network settings inspected  •  Automatic backup before every change";

            RefreshStackChips();
            RefreshAdaptersView();
            ApplyProfileToControls(_activeProfile);
        }
        catch (Exception ex)
        {
            ShowToast(InfoBarSeverity.Error, "Could not scan network settings", ex.Message, autoDismiss: false);
        }
    }

    private void ThroughputTimer_Tick(object? sender, object e)
    {
        if (string.IsNullOrWhiteSpace(_activeAdapterGuid) || _lastReport is null) return;
        NetworkAdapterInfo? primary = _lastReport.Adapters.FirstOrDefault(a => a.InterfaceGuid == _activeAdapterGuid);
        if (primary is null || !primary.IsUp) return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        double secs = Math.Max(0.5, (now - _lastSampleAt).TotalSeconds);
        (long rx, long tx) = HostDiscoveryService.SampleAdapterBytes(_activeAdapterGuid);

        if (rx >= _lastRxBytes && tx >= _lastTxBytes)
        {
            double rxBps = (rx - _lastRxBytes) / secs;
            double txBps = (tx - _lastTxBytes) / secs;
            KpiLinkText.Text = $"{primary.LinkSpeedMbps} Mbps • ↓ {FormatRate(rxBps)} ↑ {FormatRate(txBps)}";

            _sparklineSamples.Enqueue(rxBps + txBps);
            while (_sparklineSamples.Count > 30) _sparklineSamples.Dequeue();
            RenderSparkline();
        }

        _lastRxBytes = rx;
        _lastTxBytes = tx;
        _lastSampleAt = now;
    }

    private void RenderSparkline()
    {
        if (_sparklineSamples.Count < 2) return;
        double max = Math.Max(1024.0, _sparklineSamples.Max());
        const double width = 160.0;
        const double height = 14.0;
        var points = new PointCollection();
        int count = _sparklineSamples.Count;
        int i = 0;
        foreach (double val in _sparklineSamples)
        {
            double x = (i * width) / Math.Max(1, count - 1);
            double y = height - Math.Min(height, (val / max) * height);
            points.Add(new Point(x, y));
            i++;
        }
        ThroughputSparkline.Points = points;
    }

    private static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec >= 1_048_576) return $"{bytesPerSec / 1_048_576:0.0} MB/s";
        if (bytesPerSec >= 1024) return $"{bytesPerSec / 1024:0} KB/s";
        return $"{bytesPerSec:0} B/s";
    }

    private string? FindParam(string id) =>
        _lastReport?.Parameters.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.CurrentValue;

    private void RefreshStackChips()
    {
        if (_lastReport is null) return;

        string autoTuning = FindParam("tcp.autotuning") ?? "N/A";
        string rss = FindParam("offload.rss") ?? "N/A";
        string rsc = FindParam("offload.rsc") ?? "N/A";
        string tfo = FindParam("tcp.fastopen") ?? "N/A";
        string ecn = FindParam("tcp.ecn") ?? "N/A";
        string ts = FindParam("tcp.timestamps") ?? "N/A";
        string loopback = FindParam("tcp.loopback_large_mtu") ?? "N/A";
        string throttle = FindParam("reg.network_throttling") ?? "NotSet";

        bool throttleOff = throttle.Equals("0xffffffff", StringComparison.OrdinalIgnoreCase);

        var chips = new List<StackChipRow>
        {
            MakeChip("TCP Auto-Tuning", "Receive window scaling", autoTuning, autoTuning.Equals("Normal", StringComparison.OrdinalIgnoreCase)),
            MakeChip("RSS / RSC Offloads", "Multi-core & coalescing", $"{rss} / {rsc}", rss.Equals("Enabled", StringComparison.OrdinalIgnoreCase)),
            MakeChip("TCP Fast Open", "0-RTT SYN handshake", tfo, tfo.Equals("Enabled", StringComparison.OrdinalIgnoreCase)),
            MakeChip("ECN & Timestamps", "Header overhead & signaling", $"{ecn} / {ts}", ts.Equals("Disabled", StringComparison.OrdinalIgnoreCase)),
            MakeChip("Loopback Large MTU", "Win11 24H2+ BBR2 guard", loopback, loopback != "N/A"),
            MakeChip("Multimedia Throttling", "Background packet limit", throttleOff ? "Disabled (Max)" : throttle, throttleOff)
        };

        StackChipsRepeater.ItemsSource = chips;
    }

    private static StackChipRow MakeChip(string label, string sub, string value, bool healthy)
    {
        if (value == "N/A")
        {
            return new StackChipRow(label, sub, value, MutedBrush, DownBgBrush, DownBorderBrush, MutedBrush);
        }

        return new StackChipRow(
            Label: label,
            Subtitle: sub,
            ValueText: value,
            DotBrush: healthy ? GreenBrush : AmberBrush,
            BadgeBg: healthy ? UpBgBrush : AmberBgBrush,
            BadgeBorder: healthy ? UpBorderBrush : AmberBorderBrush,
            BadgeFg: healthy ? GreenBrush : AmberBrush);
    }

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
                StatusBorder: a.IsUp ? UpBorderBrush : DownBorderBrush,
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

        _syncingControls = true;
        DriverPropsRepeater.ItemsSource = props
            .Select(p => new DriverPropRow(p.DisplayName, p.RegistryKeyword, p.DisplayValue, p.ValidDisplayValues))
            .ToList();
        _syncingControls = false;
    }

    private void DriverPropCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || sender is not ComboBox box || box.Tag is not string keyword || box.SelectedItem is not string val) return;
        NetworkAdapterInfo? nic = GetSelectedAdapter();
        if (nic is null) return;

        _customOverrides[$"drv.{nic.InterfaceGuid}.{keyword}"] = val;
        SetActiveProfilePill(ProfileKind.Custom);
        UpdatePendingSummary(ProfileKind.Custom);
    }

    // ==================== PROFILES, SLIDER (3A) & MODIFIED INDICATORS ====================

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
        string? congVal = (CmbCongestion.SelectedItem as SettingChoice)?.Value;
        bool bbr2 = string.Equals(congVal, "BBR2", StringComparison.OrdinalIgnoreCase);

        IReadOnlyDictionary<string, string> target = kind == ProfileKind.Custom
            ? _customOverrides
            : _profileEngine.BuildTargetValues(kind, _lastReport, nic, preferBbr2: kind != ProfileKind.WindowsDefault && kind != ProfileKind.Current && bbr2);

        if (kind != ProfileKind.Custom)
        {
            _customOverrides = new Dictionary<string, string>(target, StringComparer.OrdinalIgnoreCase);
        }

        SyncDropdownsFromOverrides(nic);
        UpdatePendingSummary(kind);
    }

    private void SyncDropdownsFromOverrides(NetworkAdapterInfo? nic)
    {
        string GetTarget(string key, string fallback) => _customOverrides.TryGetValue(key, out string? v) ? v : fallback;

        _syncingControls = true;
        SelectChoice(CmbAutoTuning, GetTarget("tcp.autotuning", "Normal"));
        SelectChoice(CmbHeuristics, GetTarget("tcp.heuristics", "Disabled"));
        SelectChoice(CmbCongestion, GetTarget("tcp.congestion", "CUBIC"));
        SelectChoice(CmbEcn, GetTarget("tcp.ecn", "Disabled"));
        SelectChoice(CmbRss, GetTarget("offload.rss", "Enabled"));
        SelectChoice(CmbRsc, GetTarget("offload.rsc", "Enabled"));
        SelectChoice(CmbFastOpen, GetTarget("tcp.fastopen", "Enabled"));
        SelectChoice(CmbTimestamps, GetTarget("tcp.timestamps", "Disabled"));
        SelectChoice(CmbTtl, GetTarget("reg.default_ttl", "64"));
        SelectChoice(CmbLoopbackMtu, GetTarget("tcp.loopback_large_mtu", "Enabled"));

        SelectChoice(CmbThrottling, GetTarget("reg.network_throttling", "0xffffffff"));
        SelectChoice(CmbResponsiveness, GetTarget("reg.system_responsiveness", "10"));
        SelectChoice(CmbAfdFastSend, GetTarget("reg.afd_fast_send", "1500"));

        SelectChoice(CmbMaxSyn, GetTarget("tcp.maxsynretx", "2"));
        SelectChoice(CmbNonSack, GetTarget("tcp.nonsackrtt", "Disabled"));
        SelectChoice(CmbInitialRto, GetTarget("tcp.initialrto", "2000"));
        SelectChoice(CmbMinRto, GetTarget("tcp.minrto", "300"));
        SelectChoice(CmbPacing, GetTarget("tcp.pacing", "Off"));
        SelectChoice(CmbHyStart, GetTarget("tcp.hystart", "Enabled"));

        SelectChoice(CmbMaxUserPort, GetTarget("reg.max_user_port", "65534"));
        SelectChoice(CmbTimedWait, GetTarget("reg.tcp_timed_wait_delay", "30"));
        SelectChoice(CmbQosLimit, GetTarget("reg.qos_non_best_effort", "0"));
        SelectChoice(CmbQosNla, GetTarget("reg.qos_do_not_use_nla", "1"));

        if (nic is not null)
        {
            SelectChoice(CmbAckFreq, GetTarget($"nic.{nic.InterfaceGuid}.tcpackfreq", "NotSet"));
            SelectChoice(CmbNoDelay, GetTarget($"nic.{nic.InterfaceGuid}.tcpnodelay", "NotSet"));
            SelectChoice(CmbDelAckTicks, GetTarget($"nic.{nic.InterfaceGuid}.tcpdelackticks", "NotSet"));
        }
        _syncingControls = false;
    }

    private void UpdatePendingSummary(ProfileKind kind)
    {
        if (_lastReport is null) return;
        IReadOnlyList<ChangePlanItem> diffs = _diffEngine.ComputeDiff(_lastReport, _customOverrides);
        int totalTracked = Math.Max(1, _customOverrides.Count);
        int diffCount = diffs.Count;
        int alignedPct = (int)Math.Round(Math.Max(0, (totalTracked - diffCount) * 100.0 / totalTracked));

        string friendly = kind switch
        {
            ProfileKind.RecommendedOptimal => "Optimal",
            ProfileKind.GamingLowLatency => "Gaming",
            ProfileKind.WindowsDefault => "Windows Default",
            _ => kind.ToString()
        };

        if (diffCount == 0)
        {
            KpiDriftText.Text = "100% Aligned";
            KpiDriftIcon.Foreground = GreenBrush;
            KpiDriftIconBox.Background = UpBgBrush;
            KpiDriftIconBox.BorderBrush = UpBorderBrush;
            KpiDriftHeader.Foreground = GreenBrush;
            ApplyChangesButton.Content = "All Settings Aligned";
            ApplyChangesButton.IsEnabled = false;
        }
        else
        {
            string word = diffCount == 1 ? "Change" : "Changes";
            KpiDriftText.Text = $"{alignedPct}% • {diffCount} {word}";
            KpiDriftIcon.Foreground = AccentBrush;
            KpiDriftIconBox.Background = AccentBgBrush;
            KpiDriftIconBox.BorderBrush = AccentBorderBrush;
            KpiDriftHeader.Foreground = AccentBrush;
            ApplyChangesButton.Content = $"Review & Apply ({diffCount})";
            ApplyChangesButton.IsEnabled = true;
        }

        KpiProfileLabel.Text = $"Profile: {friendly}";
        HighlightModifiedSettingCards(diffs);
    }

    private void HighlightModifiedSettingCards(IReadOnlyList<ChangePlanItem> diffs)
    {
        var changedIds = new HashSet<string>(diffs.Select(d => d.ParameterId), StringComparer.OrdinalIgnoreCase);
        NetworkAdapterInfo? nic = GetSelectedAdapter();

        foreach (KeyValuePair<string, Border> kvp in _settingCardsByKey)
        {
            string effectiveKey = kvp.Key;
            if (effectiveKey.StartsWith("nic.", StringComparison.OrdinalIgnoreCase) && nic is not null)
            {
                effectiveKey = effectiveKey.Replace("nic.", $"nic.{nic.InterfaceGuid}.");
            }

            bool isModified = changedIds.Contains(effectiveKey);
            kvp.Value.BorderBrush = isModified ? AccentBorderBrush : SubtleBorderBrush;
            kvp.Value.BorderThickness = isModified ? new Thickness(2, 1, 1, 1) : new Thickness(1);
        }
    }

    private static void SelectChoice(ComboBox box, string value)
    {
        if (box.ItemsSource is IEnumerable<SettingChoice> items)
        {
            foreach (SettingChoice item in items)
            {
                if (string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedItem = item;
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

    private void RecommendedSettings_Click(object sender, RoutedEventArgs e)
    {
        SetActiveProfilePill(ProfileKind.RecommendedOptimal);
        ApplyProfileToControls(ProfileKind.RecommendedOptimal);
        int count = _lastReport is null ? 0 : _diffEngine.ComputeDiff(_lastReport, _customOverrides).Count;
        if (count == 0)
        {
            ShowToast(InfoBarSeverity.Success, "Already Using Recommended Settings", "Your Windows network settings already match the recommended configuration.");
        }
        else
        {
            ShowToast(InfoBarSeverity.Informational, "Recommended Settings Loaded", $"{count} recommended change(s) are ready. Click Review & Apply when you want to apply them.");
        }
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        SetActiveProfilePill(ProfileKind.WindowsDefault);
        ApplyProfileToControls(ProfileKind.WindowsDefault);
        int count = _lastReport is null ? 0 : _diffEngine.ComputeDiff(_lastReport, _customOverrides).Count;
        if (count == 0)
        {
            ShowToast(InfoBarSeverity.Success, "Already at Windows Defaults", "Your network settings already match Windows defaults.");
        }
        else
        {
            ShowToast(InfoBarSeverity.Informational, "Windows Defaults Loaded", $"{count} change(s) staged to restore Windows defaults. Click Review & Apply to confirm.");
        }
    }

    private void InteractiveCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || sender is not ComboBox box || box.Tag is not string key || box.SelectedItem is not SettingChoice choice) return;

        _customOverrides[key] = choice.Value;
        if (key == "tcp.congestion" && _lastReport is not null)
        {
            string loopbackTarget = string.Equals(choice.Value, "BBR2", StringComparison.OrdinalIgnoreCase) && _lastReport.OsInfo.RequiresBbr2LoopbackGuard
                ? "Disabled"
                : "Enabled";
            _customOverrides["tcp.loopback_large_mtu"] = loopbackTarget;
            _syncingControls = true;
            SelectChoice(CmbLoopbackMtu, loopbackTarget);
            _syncingControls = false;
        }

        SetActiveProfilePill(ProfileKind.Custom);
        UpdatePendingSummary(ProfileKind.Custom);
    }

    private void InteractiveNicCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || sender is not ComboBox box || box.Tag is not string suffix || box.SelectedItem is not SettingChoice choice) return;
        NetworkAdapterInfo? nic = GetSelectedAdapter();
        if (nic is null) return;

        string key = suffix.Replace("nic.", $"nic.{nic.InterfaceGuid}.");
        _customOverrides[key] = choice.Value;

        SetActiveProfilePill(ProfileKind.Custom);
        UpdatePendingSummary(ProfileKind.Custom);
    }

    // Option 3A: Live real-time speed slider tunes settings immediately & switches to Custom
    private void SpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (SpeedLabelText is null) return;
        int mbps = (int)e.NewValue;
        int mtu = int.TryParse(MtuBox?.Text, out int parsedMtu) && parsedMtu >= 576 ? parsedMtu : 1500;
        BdpResult bdp = BdpCalculator.Calculate(mbps, BandwidthUnit.Mbps, 40, mtu);
        string speedStr = mbps >= 1000 ? $"{mbps / 1000.0:0.#} Gbps" : $"{mbps} Mbps";

        if (_lastReport is not null && !_syncingControls)
        {
            IReadOnlyDictionary<string, string> tierOverrides = _profileEngine.BuildSpeedTierOverrides(mbps);
            foreach (KeyValuePair<string, string> kvp in tierOverrides)
            {
                _customOverrides[kvp.Key] = kvp.Value;
            }

            SyncDropdownsFromOverrides(GetSelectedAdapter());
            SetActiveProfilePill(ProfileKind.Custom);
            UpdatePendingSummary(ProfileKind.Custom);
            SpeedLabelText.Text = $"{speedStr}  •  {tierOverrides.Count} settings tuned  •  RWIN {bdp.SuggestedRwinBytes / 1024.0:N0} KB";
        }
        else
        {
            SpeedLabelText.Text = $"{speedStr}  •  RWIN {bdp.SuggestedRwinBytes / 1024.0:N0} KB @ 40ms";
        }
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

    private void MtuBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        NetworkAdapterInfo? nic = GetSelectedAdapter();
        if (nic is null) return;
        if (int.TryParse(MtuBox.Text.Trim(), out int mtu) && mtu >= 576 && mtu <= 9000)
        {
            if (mtu != nic.Ipv4Mtu)
            {
                _customOverrides[$"nic.{nic.InterfaceGuid}.mtu"] = mtu.ToString();
                SetActiveProfilePill(ProfileKind.Custom);
                UpdatePendingSummary(ProfileKind.Custom);
            }
        }
        else
        {
            MtuBox.Text = nic.Ipv4Mtu.ToString();
            ShowToast(InfoBarSeverity.Warning, "Invalid MTU value", "Enter a number between 576 and 9000 (standard is 1500, PPPoE is 1492).");
        }
    }

    private void PppoeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        string targetMtu = PppoeCheck.IsChecked == true ? "1492" : "1500";
        MtuBox.Text = targetMtu;
        NetworkAdapterInfo? nic = GetSelectedAdapter();
        if (nic is not null)
        {
            _customOverrides[$"nic.{nic.InterfaceGuid}.mtu"] = targetMtu;
            SetActiveProfilePill(ProfileKind.Custom);
            UpdatePendingSummary(ProfileKind.Custom);
        }
    }

    private async void RescanButton_Click(object sender, RoutedEventArgs e)
    {
        await RunHostScanAsync();
        ShowToast(InfoBarSeverity.Informational, "Network scan refreshed", "All live settings and adapter details are up to date.");
    }

    // ==================== APPLY, PROFILE SAVE/LOAD & SCRIPT EXPORT ====================

    private async void PreviewSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null) return;
        IReadOnlyList<ChangePlanItem> diffs = _diffEngine.ComputeDiff(_lastReport, _customOverrides);
        if (diffs.Count == 0)
        {
            ShowToast(InfoBarSeverity.Success, "Already optimized", "Your current Windows settings already match this profile.");
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

    private async void SaveCustomProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_customOverrides.Count == 0) return;
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluentNet", "Profiles");
        Directory.CreateDirectory(dir);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string filePath = Path.Combine(dir, $"FluentNet-Profile-{stamp}.json");
        var doc = new CustomProfileDocument($"Custom-{stamp}", DateTimeOffset.UtcNow, _appVersion, new Dictionary<string, string>(_customOverrides));
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(doc, FluentNetJsonContext.Default.CustomProfileDocument));
        ShowToast(InfoBarSeverity.Success, "Profile saved", $"Saved to {Path.GetFileName(filePath)} in your FluentNet Profiles folder.");
    }

    private async void LoadCustomProfile_Click(object sender, RoutedEventArgs e)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluentNet", "Profiles");
        if (!Directory.Exists(dir))
        {
            ShowToast(InfoBarSeverity.Informational, "No saved profiles yet", "Click Save Profile first to create a reusable profile.");
            return;
        }

        string[] files = Directory.GetFiles(dir, "*.json").OrderByDescending(File.GetCreationTimeUtc).ToArray();
        if (files.Length == 0)
        {
            ShowToast(InfoBarSeverity.Informational, "No saved profiles yet", "Click Save Profile first to create a reusable profile.");
            return;
        }

        var list = new ListView
        {
            ItemsSource = files.Select(Path.GetFileName).ToList(),
            SelectedIndex = 0,
            MaxHeight = 260
        };

        var dlg = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
            Title = "Load Saved Profile",
            Content = list,
            PrimaryButtonText = "Load Profile",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dlg.ShowAsync() == ContentDialogResult.Primary && list.SelectedIndex >= 0)
        {
            string json = await File.ReadAllTextAsync(files[list.SelectedIndex]);
            CustomProfileDocument? loaded = JsonSerializer.Deserialize(json, FluentNetJsonContext.Default.CustomProfileDocument);
            if (loaded is not null && loaded.Settings.Count > 0)
            {
                _customOverrides = new Dictionary<string, string>(loaded.Settings, StringComparer.OrdinalIgnoreCase);
                SyncDropdownsFromOverrides(GetSelectedAdapter());
                SetActiveProfilePill(ProfileKind.Custom);
                UpdatePendingSummary(ProfileKind.Custom);
                ShowToast(InfoBarSeverity.Success, "Profile loaded", $"Loaded {loaded.Settings.Count} settings from {loaded.ProfileName}. Click Review & Apply when ready.");
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
        ShowToast(InfoBarSeverity.Success, "Scripts exported to Desktop", $"Saved .ps1 and .reg files inside FluentNet-Exports.");
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
                ShowToast(InfoBarSeverity.Warning, "Administrator permission required", "Run FluentNet as Administrator, or place FluentNet.Cli.exe in the app folder.", autoDismiss: false);
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
            catch (Win32Exception)
            {
                ShowToast(InfoBarSeverity.Informational, "Changes cancelled", "No settings were modified.");
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
            int rebootCount = diffs.Count(d => d.RequiresReboot);
            int instantCount = diffs.Count - rebootCount;
            string settingWord = diffs.Count == 1 ? "setting" : "settings";

            if (rebootCount == 0)
            {
                ShowToast(InfoBarSeverity.Success,
                    $"{diffs.Count} {settingWord} applied and active now",
                    "All changes took effect immediately—no restart needed.");
            }
            else if (instantCount == 0)
            {
                ShowToast(InfoBarSeverity.Success,
                    $"{diffs.Count} {settingWord} saved — restart Windows to apply",
                    "These Windows settings will become active after your next reboot.");
            }
            else
            {
                ShowToast(InfoBarSeverity.Success,
                    $"{diffs.Count} {settingWord} applied",
                    $"{instantCount} active immediately; restart Windows to finish applying {rebootCount} registry {(rebootCount == 1 ? "change" : "changes")}.");
            }
        }
        else
        {
            List<ApplyItemResult> failed = report?.Results.Where(r => !r.Succeeded).ToList() ?? new List<ApplyItemResult>();
            string detail = failed.Count > 0
                ? string.Join(" • ", failed.Take(2).Select(f => $"{f.ParameterId}: {f.Message}"))
                : "One or more settings could not be updated by Windows.";
            ShowToast(InfoBarSeverity.Error,
                failed.Count > 0 ? $"{failed.Count} {(failed.Count == 1 ? "setting" : "settings")} could not be applied" : "Update failed",
                detail,
                autoDismiss: false);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore cleanup */ }
    }

    // ==================== DNS SWITCHER, REPAIR & DIAGNOSTICS ====================

    private async void ApplyDnsPreset_Click(object sender, RoutedEventArgs e)
    {
        if (DnsPresetCombo.SelectedItem is not SettingChoice choice) return;
        await ApplyDnsForSelectedAdapterAsync(choice.Value, choice.Label);
    }

    private async void ApplyFastestDns_Click(object sender, RoutedEventArgs e)
    {
        if (_fastestDns is null || !_fastestDns.Reachable) return;
        string servers = $"{_fastestDns.Preset.PrimaryIpv4},{_fastestDns.Preset.SecondaryIpv4}";
        await ApplyDnsForSelectedAdapterAsync(servers, _fastestDns.Preset.Name);
    }

    private async Task ApplyDnsForSelectedAdapterAsync(string serverList, string friendlyName)
    {
        NetworkAdapterInfo? nic = GetSelectedAdapter();
        if (nic is null || nic.InterfaceIndex <= 0)
        {
            ShowToast(InfoBarSeverity.Warning, "No active adapter selected", "Select an active Wi-Fi or Ethernet adapter first.");
            return;
        }

        string currentDns = nic.DnsServers is { Count: > 0 }
            ? string.Join(",", nic.DnsServers.Where(d => !d.Contains(':')).Take(2))
            : "DHCP";
        if (string.IsNullOrWhiteSpace(currentDns)) currentDns = "DHCP";

        var item = new ChangePlanItem(
            ParameterId: $"nic.{nic.InterfaceGuid}.dns",
            DisplayName: $"[{nic.Alias}] IPv4 DNS ({friendlyName})",
            OldValue: currentDns,
            NewValue: serverList,
            WindowsDefaultValue: "DHCP",
            ExactPath: $"MSFT_NetClientDnsServerAddress[ifIndex={nic.InterfaceIndex}].ServerAddresses",
            SourceKind: ParameterSourceKind.Cim,
            RequiresReboot: false);

        await ApplyChangesNativeOrElevatedAsync(new[] { item }, $"DNS ({friendlyName})");
    }

    private void FlushDns_Click(object sender, RoutedEventArgs e)
    {
        bool ok = DnsOptimizerService.FlushDnsCache();
        if (ok)
        {
            ShowToast(InfoBarSeverity.Success, "DNS cache cleared", "Fresh domain lookups are active immediately.");
        }
        else
        {
            ShowToast(InfoBarSeverity.Warning, "Could not clear DNS cache", "Windows DNS Client service may be stopped.");
        }
    }

    private void FlushArp_Click(object sender, RoutedEventArgs e)
    {
        NetworkAdapterInfo? nic = GetSelectedAdapter();
        if (nic is null || nic.InterfaceIndex <= 0)
        {
            ShowToast(InfoBarSeverity.Warning, "No active adapter", "Connect to a network before flushing the ARP table.");
            return;
        }

        bool ok = DnsOptimizerService.FlushArpCache(nic.InterfaceIndex);
        if (ok)
        {
            ShowToast(InfoBarSeverity.Success, "ARP cache cleared", $"Local address cache cleared for {nic.Alias}.");
        }
        else
        {
            ShowToast(InfoBarSeverity.Warning, "Administrator rights needed", "Run FluentNet as Administrator to flush the ARP table.");
        }
    }

    private async void RenewDhcp_Click(object sender, RoutedEventArgs e)
    {
        ShowToast(InfoBarSeverity.Informational, "Renewing IP address...", "Requesting a fresh DHCP lease from your router.");
        (bool ok, string msg) = await DnsOptimizerService.RenewDhcpLeaseAsync();
        await RunHostScanAsync();
        ShowToast(ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning, ok ? "IP lease renewed" : "DHCP renew skipped", msg);
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

    private async void RunDnsBenchmark_Click(object sender, RoutedEventArgs e)
    {
        await RunDiagnosticAsync(async ct =>
        {
            IReadOnlyList<DnsBenchmarkResult> results = await _dns.BenchmarkResolversAsync(cancellationToken: ct);
            _fastestDns = results.FirstOrDefault(r => r.Reachable);
            ApplyFastestDnsButton.IsEnabled = _fastestDns is not null;
            if (_fastestDns is not null)
            {
                ApplyFastestDnsButton.Content = $"Use {_fastestDns.Preset.Name}";
            }

            var lines = new List<string> { "[DNS BENCHMARK] Ranked fastest TCP/53 connect latency:" };
            for (int i = 0; i < results.Count; i++)
            {
                DnsBenchmarkResult r = results[i];
                lines.Add($"  #{i + 1}  {r.Preset.Name,-18} ({r.Preset.PrimaryIpv4,-14} / {r.Preset.SecondaryIpv4,-15})  ->  {r.StatusText,-10}  [{r.Preset.Tagline}]");
            }
            return string.Join(Environment.NewLine, lines);
        });
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
                $"[LATENCY] {r.Host,-20} | Grade:{DnsOptimizerService.GradeLatencySweep(r),-2} | Loss:{r.PacketLossPercent}% | Min:{r.MinMs}ms Avg:{r.AvgMs}ms Max:{r.MaxMs}ms Jitter:{r.JitterMs}ms"));
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
        ShowToast(InfoBarSeverity.Success, "Copied to clipboard", "Diagnostic results are ready to paste.");
    }

    // ==================== MULTI-SELECT SNAPSHOTS (ITEM 6) ====================

    private async Task RefreshSnapshotsAsync()
    {
        IReadOnlyList<SnapshotSummary> summaries = await _snapshotManager.ListSnapshotSummariesAsync();
        _snapshotRows = summaries.Select(s => new SnapshotDisplayRow(
            filePath: s.FilePath,
            snapshotId: s.SnapshotId,
            profileName: s.ProfileName,
            changeCountLabel: s.ChangeCount == 1 ? "1 setting" : $"{s.ChangeCount} settings",
            osBuildLabel: $"Build {s.OsBuild}",
            timestampLocal: s.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))).ToList();

        SnapshotsRepeater.ItemsSource = _snapshotRows;
        SnapshotsEmptyText.Visibility = _snapshotRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _syncingControls = true;
        SelectAllSnapshotsCheck.IsChecked = false;
        _syncingControls = false;
        UpdateSnapshotActionButtons();
    }

    private void SnapshotCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        UpdateSnapshotActionButtons();
    }

    private void SelectAllSnapshots_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        bool checkAll = SelectAllSnapshotsCheck.IsChecked == true;
        _syncingControls = true;
        foreach (SnapshotDisplayRow row in _snapshotRows)
        {
            row.IsSelected = checkAll;
        }
        _syncingControls = false;
        UpdateSnapshotActionButtons();
    }

    private void UpdateSnapshotActionButtons()
    {
        int selectedCount = _snapshotRows.Count(r => r.IsSelected);
        DeleteSnapshotsButton.IsEnabled = selectedCount > 0;
        DeleteSnapshotsButton.Content = selectedCount > 0 ? $"Delete ({selectedCount})" : "Delete Selected";
        InspectSnapshotButton.IsEnabled = selectedCount == 1;
        RollbackSnapshotButton.IsEnabled = selectedCount == 1;
    }

    private async void RefreshSnapshots_Click(object sender, RoutedEventArgs e) => await RefreshSnapshotsAsync();

    private async void InspectSnapshot_Click(object sender, RoutedEventArgs e)
    {
        SnapshotDisplayRow? row = _snapshotRows.FirstOrDefault(r => r.IsSelected);
        if (row is null) return;

        SnapshotManifest? snap = await _snapshotManager.LoadSnapshotAsync(row.FilePath);
        if (snap is null) return;

        string summary = string.Join(Environment.NewLine, snap.Items.Select(i =>
            $"• {i.DisplayName}: {i.OldValue} → {i.NewValue}"));

        var dlg = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
            Title = $"Snapshot Details — {snap.ProfileName}",
            Content = new ScrollViewer
            {
                MaxHeight = 360,
                Content = new TextBlock
                {
                    Text = $"Created: {snap.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\nWindows Build: {snap.OsBuild}\n\nStored changes (restoring reverses these):\n{summary}",
                    FontFamily = new FontFamily("Cascadia Code, Consolas"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                }
            },
            CloseButtonText = "Close"
        };
        await dlg.ShowAsync();
    }

    private async void DeleteSelectedSnapshots_Click(object sender, RoutedEventArgs e)
    {
        List<SnapshotDisplayRow> selected = _snapshotRows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0) return;

        if (selected.Count > 1)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
                Title = $"Delete {selected.Count} snapshots?",
                Content = "Deleted backup snapshots cannot be recovered.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }

        foreach (SnapshotDisplayRow row in selected)
        {
            _snapshotManager.DeleteSnapshot(row.FilePath);
        }

        await RefreshSnapshotsAsync();
        ShowToast(InfoBarSeverity.Success,
            selected.Count == 1 ? "Snapshot deleted" : $"{selected.Count} snapshots deleted",
            "Selected backup files were removed.");
    }

    private async void RollbackSelected_Click(object sender, RoutedEventArgs e)
    {
        SnapshotDisplayRow? row = _snapshotRows.FirstOrDefault(r => r.IsSelected);
        if (row is null) return;

        SnapshotManifest? snap = await _snapshotManager.LoadSnapshotAsync(row.FilePath);
        bool needsReboot = snap?.Items.Any(i => i.RequiresReboot) ?? false;

        if (ElevationLauncher.IsProcessElevated())
        {
            if (snap is null || snap.Items.Count == 0) return;
            List<ChangePlanItem> revert = snap.Items.Select(i => i with { OldValue = i.NewValue, NewValue = i.OldValue }).ToList();
            ApplyReport res = await _applyEngine.ApplyPlanAsync(revert);
            await RunHostScanAsync();
            if (res.AllSucceeded)
            {
                ShowToast(InfoBarSeverity.Success,
                    needsReboot ? "Settings restored — restart Windows to finish" : "Settings restored and active now",
                    $"Reverted {revert.Count} {(revert.Count == 1 ? "setting" : "settings")} from {row.ProfileName} backup.");
            }
            else
            {
                ShowToast(InfoBarSeverity.Error, "Restore completed with errors",
                    string.Join(" • ", res.Results.Where(r => !r.Succeeded).Take(2).Select(r => $"{r.ParameterId}: {r.Message}")),
                    autoDismiss: false);
            }
            return;
        }

        string? cli = ElevationLauncher.ResolveCliPath();
        if (cli is null) return;
        int code = await Task.Run(() => ElevationLauncher.RunElevatedCli(cli, $"rollback --snapshot \"{row.FilePath}\"", hideWindow: true));
        await RunHostScanAsync();
        if (code == 0)
        {
            ShowToast(InfoBarSeverity.Success,
                needsReboot ? "Settings restored — restart Windows to finish" : "Settings restored and active now",
                $"Reverted your system back to the {row.ProfileName} backup.");
        }
        else
        {
            ShowToast(InfoBarSeverity.Error, "Restore failed", $"One or more settings could not be restored (code {code}).", autoDismiss: false);
        }
    }

    // ==================== APP SETTINGS & UPDATES ====================

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
        ShowToast(InfoBarSeverity.Informational, $"Downloading FluentNet v{res.LatestVersion}...", "Verifying package integrity automatically.");

        try
        {
            UpdateDownloadResult dl = await _updater.DownloadReleasePackageAsync(res);
            ShowToast(dl.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                dl.Succeeded ? "Update downloaded" : "Download failed",
                dl.Message,
                autoDismiss: dl.Succeeded);
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
