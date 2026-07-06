using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.Localization;
using BasisPM.Core.Models;

namespace BasisPM.App.Views;

/// <summary>Where a created bundle goes: a local file the user keeps, or a GitHub submission.</summary>
public enum BundleDestination { SaveToFile, Submit }

/// <summary>Result of the create-bundle dialog: a name, description, the chosen packages, and where it's headed.</summary>
public sealed record BundleDraft(string Name, string Description, List<BundlePackage> Packages, BundleDestination Destination);

/// <summary>One selectable candidate package in the create-bundle dialog.</summary>
public sealed class PackagePick
{
    public bool Include { get; set; } = true;
    public string Title { get; }
    public string Subtitle { get; }
    public BundlePackage Package { get; }

    public PackagePick(BundlePackage p)
    {
        Package = p;
        Title = string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name!;
        Subtitle = p.GitUrl is not null ? $"{p.Id}  ·  git"
                 : p.Version is not null ? $"{p.Id}  ·  {p.Version}"
                 : p.Id;
    }
}

public partial class CreateBundleWindow : Window
{
    private readonly ObservableCollection<PackagePick> _picks = new();

    public CreateBundleWindow()
    {
        InitializeComponent();
    }

    public CreateBundleWindow(string suggestedName, string basisLine, IEnumerable<BundlePackage> candidates) : this()
    {
        NameBox.Text = suggestedName;
        BasisLine.Text = basisLine;
        foreach (var c in candidates) _picks.Add(new PackagePick(c));
        PackageList.ItemsSource = _picks;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnSaveLocal(object? sender, RoutedEventArgs e) => TryClose(BundleDestination.SaveToFile);

    private void OnSubmit(object? sender, RoutedEventArgs e) => TryClose(BundleDestination.Submit);

    // Validates the form, then closes with the draft bound for the chosen destination.
    private void TryClose(BundleDestination destination)
    {
        var name = (NameBox.Text ?? "").Trim();
        var chosen = _picks.Where(p => p.Include).Select(p => p.Package).ToList();
        if (string.IsNullOrWhiteSpace(name)) { ShowError(L.Tr("dialog.createBundle.errorNoName")); return; }
        if (chosen.Count == 0) { ShowError(L.Tr("dialog.createBundle.errorNoPackages")); return; }
        Close(new BundleDraft(name, (DescBox.Text ?? "").Trim(), chosen, destination));
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.IsVisible = true;
    }
}
