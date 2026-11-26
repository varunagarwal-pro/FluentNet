using System.Collections.Generic;
using System.Windows;
using FluentNet.Core.Models;

namespace FluentNet.Classic;

public enum DiffModalAction
{
    Cancel,
    ExportScripts,
    ApplyUac
}

public partial class DiffPreviewWindow : Window
{
    public DiffModalAction SelectedAction { get; private set; } = DiffModalAction.Cancel;

    public DiffPreviewWindow(string profileName, IReadOnlyList<ChangePlanItem> items)
    {
        InitializeComponent();
        HeaderTitle.Text = $"Review Changes — {profileName} ({items.Count} items)";
        ChangesGrid.ItemsSource = items;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = DiffModalAction.ApplyUac;
        DialogResult = true;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = DiffModalAction.ExportScripts;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = DiffModalAction.Cancel;
        DialogResult = false;
    }
}
