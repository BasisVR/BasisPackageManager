using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.Core.Models;

namespace BasisPM.App.Views;

public partial class VersionPickerWindow : Window
{
    private IReadOnlyList<PackageVersionOption> _all = new List<PackageVersionOption>();

    public VersionPickerWindow() => InitializeComponent();

    public VersionPickerWindow(string title, PackageVersions versions) : this()
    {
        TitleText.Text = title;
        _all = versions.Options;
        SubText.Text = versions.HasReleases
            ? "Pick a published release, or the latest default branch."
            : "No releases published — showing tags / the default branch. Ask the creator to cut releases for stable versions.";
        // If there's no stable option, show prereleases by default so the list isn't empty.
        ShowPre.IsChecked = !_all.Any(o => !o.IsPrerelease && o.Kind != VersionKind.Branch);
        Rebuild();
        List.DoubleTapped += (_, _) => Close(List.SelectedItem as PackageVersionOption);
    }

    private void OnShowPreChanged(object? sender, RoutedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        if (List is null) return;
        var showPre = ShowPre.IsChecked == true;
        var items = _all.Where(o => showPre || !o.IsPrerelease).ToList();
        List.ItemsSource = items;
        if (items.Count > 0) List.SelectedIndex = 0;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(List.SelectedItem as PackageVersionOption);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
