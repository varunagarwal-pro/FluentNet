using System.ComponentModel;
using FluentNet.Core.Models;
using FluentNet.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FluentNet.App;

public sealed class DiffReviewRow : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public DiffReviewRow(ChangePlanItem item)
    {
        Item = item;
    }

    public ChangePlanItem Item { get; }
    public string DisplayName => Item.DisplayName;
    public string OldValue => Item.OldValue;
    public string NewValue => Item.NewValue;
    public string DefaultValue => Item.WindowsDefaultValue;
    public string ExactPath => Item.ExactPath;
    public string SourceLabel => Item.SourceKind switch
    {
        ParameterSourceKind.Registry => "HKLM 64-bit",
        ParameterSourceKind.Cim => "Native CIM",
        ParameterSourceKind.DriverAdvancedProperty => "NIC Driver",
        ParameterSourceKind.Netsh => "Netsh IOCTL",
        _ => Item.SourceKind.ToString()
    };
    public Visibility RebootVisibility => Item.RequiresReboot ? Visibility.Visible : Visibility.Collapsed;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class ReviewChangesDialog : ContentDialog
{
    private readonly List<DiffReviewRow> _rows;

    public ReviewChangesDialog(string profileName, IReadOnlyList<ChangePlanItem> items)
    {
        InitializeComponent();
        Title = $"Review Proposed Changes — {profileName}";
        PrimaryButtonText = ElevationLauncher.IsProcessElevated()
            ? "Apply Directly (.NET)"
            : "Apply Changes (UAC)";

        _rows = items.Select(i => new DiffReviewRow(i)).ToList();
        DiffRepeater.ItemsSource = _rows;

        bool needsReboot = items.Any(i => i.RequiresReboot);
        string plural = items.Count == 1 ? "setting" : "settings";
        SummaryHeadline.Text = $"{items.Count} {plural} queued for {profileName}";
        RebootBadge.Background = new SolidColorBrush(needsReboot ? Color.FromArgb(40, 245, 158, 11) : Color.FromArgb(35, 16, 185, 129));
        RebootBadgeText.Foreground = new SolidColorBrush(needsReboot ? Color.FromArgb(255, 245, 158, 11) : Color.FromArgb(255, 16, 185, 129));
        RebootBadgeText.Text = needsReboot ? "Reboot Recommended" : "Instant Apply";
    }

    public IReadOnlyList<ChangePlanItem> GetSelectedItems()
        => _rows.Where(r => r.IsSelected).Select(r => r.Item).ToList();
}
