using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.Localization;
using BasisPM.Core.Models;

namespace BasisPM.App.Views;

/// <summary>Where a created package list goes: a local file the user keeps, or a GitHub submission.</summary>
public enum PackageListDestination { SaveToFile, Submit }

/// <summary>Result of the create-package-list dialog: a name, description, the chosen packages, and where it's headed.</summary>
public sealed record PackageListDraft(string Name, string Description, List<PackageListEntry> Packages, PackageListDestination Destination);

/// <summary>One selectable candidate package in the create-package-list dialog.</summary>
public sealed class PackagePick
{
    public bool Include { get; set; } = true;
    public string Title { get; }
    public string Subtitle { get; }
    public PackageListEntry Package { get; }

    public PackagePick(PackageListEntry p)
    {
        Package = p;
        Title = string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name!;
        Subtitle = p.GitUrl is not null ? $"{p.Id}  ·  git"
                 : p.Version is not null ? $"{p.Id}  ·  {p.Version}"
                 : p.Id;
    }
}

public partial class CreatePackageListWindow : Window
{
    private readonly ObservableCollection<PackagePick> _picks = new();

    public CreatePackageListWindow()
    {
        InitializeComponent();
    }

    public CreatePackageListWindow(string suggestedName, string basisLine, IEnumerable<PackageListEntry> candidates) : this()
    {
        NameBox.Text = suggestedName;
        BasisLine.Text = basisLine;
        foreach (var c in candidates) _picks.Add(new PackagePick(c));
        PackagesItems.ItemsSource = _picks;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnSaveLocal(object? sender, RoutedEventArgs e) => TryClose(PackageListDestination.SaveToFile);

    private void OnSubmit(object? sender, RoutedEventArgs e) => TryClose(PackageListDestination.Submit);

    // Validates the form, then closes with the draft bound for the chosen destination.
    private void TryClose(PackageListDestination destination)
    {
        var name = (NameBox.Text ?? "").Trim();
        var chosen = _picks.Where(p => p.Include).Select(p => p.Package).ToList();
        if (string.IsNullOrWhiteSpace(name)) { ShowError(L.Tr("dialog.createPackageList.errorNoName")); return; }
        if (chosen.Count == 0) { ShowError(L.Tr("dialog.createPackageList.errorNoPackages")); return; }
        Close(new PackageListDraft(name, (DescBox.Text ?? "").Trim(), chosen, destination));
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.IsVisible = true;
    }
}
