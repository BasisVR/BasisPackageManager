using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.Localization;
using BasisPM.Core.Models;

namespace BasisPM.App.Views;

/// <summary>Result of the create-bundle dialog: a name, description, and the chosen packages.</summary>
public sealed record BundleDraft(string Name, string Description, List<BundlePackage> Packages);

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

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? "").Trim();
        var chosen = _picks.Where(p => p.Include).Select(p => p.Package).ToList();
        if (string.IsNullOrWhiteSpace(name)) { ShowError(L.Tr("dialog.createBundle.errorNoName")); return; }
        if (chosen.Count == 0) { ShowError(L.Tr("dialog.createBundle.errorNoPackages")); return; }
        Close(new BundleDraft(name, (DescBox.Text ?? "").Trim(), chosen));
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.IsVisible = true;
    }
}
