using System.ComponentModel;
using FluentNet.Core.Models;
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
        _rows = items.Select(i => new DiffReviewRow(i)).ToList();
        DiffRepeater.ItemsSource = _rows;

        bool needsReboot = items.Any(i => i.RequiresReboot);
        SummaryHeadline.Text = $"{items.Count} setting(s) queued for {profileName}";
        RebootBadge.Background = new SolidColorBrush(needsReboot ? Color.FromArgb(60, 245, 158, 11) : Color.FromArgb(45, 16, 185, 129));
        RebootBadgeText.Foreground = new SolidColorBrush(needsReboot ? Color.FromArgb(255, 251, 191, 36) : Color.FromArgb(255, 52, 211, 153));
        RebootBadgeText.Text = needsReboot ? "Reboot Required" : "Instant Apply";
    }

    public IReadOnlyList<ChangePlanItem> GetSelectedItems()
        => _rows.Where(r => r.IsSelected).Select(r => r.Item).ToList();
}
