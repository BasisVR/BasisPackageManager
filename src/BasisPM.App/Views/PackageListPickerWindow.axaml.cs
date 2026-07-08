using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.Localization;
using BasisPM.Core.Models;

namespace BasisPM.App.Views;

public sealed class PackageListPickerRow
{
    public PackageListPickerRow(PackageList list)
    {
        PackageList = list;
        CountLabel = L.Tr("dialog.packageListPicker.packageCount", list.Packages.Count);
    }

    public PackageList PackageList { get; }
    public string Icon => string.IsNullOrWhiteSpace(PackageList.Icon) ? "🧩" : PackageList.Icon!;
    public string Name => PackageList.Name;
    public string Description => PackageList.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(PackageList.Description);
    public string CountLabel { get; }
}

public partial class PackageListPickerWindow : Window
{
    public PackageListPickerWindow() => InitializeComponent();

    public PackageListPickerWindow(IReadOnlyList<PackageList> lists) : this()
    {
        List.ItemsSource = lists.Select(l => new PackageListPickerRow(l)).ToList();
        if (lists.Count > 0) List.SelectedIndex = 0;
        List.DoubleTapped += (_, _) => Close((List.SelectedItem as PackageListPickerRow)?.PackageList);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close((List.SelectedItem as PackageListPickerRow)?.PackageList);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
