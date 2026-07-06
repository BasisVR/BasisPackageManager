using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class PackagesView : UserControl
{
    public PackagesView() => InitializeComponent();

    // Clicking a package row opens its detail panel — but a tap that landed on one of the row's
    // buttons (Install / Manage / Website) should run that button, not open the panel.
    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (sender is Control row && row.DataContext is PackageRow pr && DataContext is PackagesViewModel vm)
            vm.OpenDetail(pr);
    }

    // Close the detail overlay when the dark backdrop itself is clicked (not the card on top of it).
    private void OnBackdropTapped(object? sender, TappedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender) && DataContext is PackagesViewModel vm)
            vm.CloseDetailCommand.Execute(null);
    }

    // The "Manage" menu for an installed package is built in code from a Flyout of themed buttons
    // (so it matches the app styling), and its commands are invoked directly — no reliance on binding
    // resolution or Fluent menu-item theming inside the popup.
    private void OnManageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not PackageRow row) return;
        if (DataContext is not PackagesViewModel vm) return;

        var panel = new StackPanel { Spacing = 2 };
        var flyout = new Flyout();

        panel.Children.Add(MenuButton(L.Tr("packages.button.update"), false, flyout, () => vm.InstallCommand.Execute(row.Entry)));
        if (row.HasGit)
            panel.Children.Add(MenuButton(L.Tr("packages.button.chooseVersion"), false, flyout, () => vm.ChooseVersionCommand.Execute(row.Entry)));
        // A git package installed but not yet mounted can be mounted for editing straight from here.
        if (row.CanMountToEdit)
            panel.Children.Add(MenuButton(L.Tr("develop.button.mountToEdit"), false, flyout, () => vm.MountCommand.Execute(row)));
        panel.Children.Add(MenuButton(L.Tr("packages.button.remove"), true, flyout, () => vm.RemoveCommand.Execute(row.Entry)));

        // The Fluent FlyoutPresenter fill is translucent, so draw the menu's own opaque, themed
        // panel here; the presenter is left chrome-less (see BasisTheme.axaml FlyoutPresenter style).
        flyout.Content = new Border
        {
            Child = panel,
            MinWidth = 168,
            Padding = new Thickness(5),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Background = FindBrush("BasisSurfaceBrush", "#1A1937"),
            BorderBrush = FindBrush("BasisBorderStrongBrush", "#33FFFFFF"),
        };

        flyout.ShowAt(control);
    }

    // Resolve a themed brush resource, falling back to an opaque literal so the popup is never
    // see-through even if lookup misses.
    private IBrush FindBrush(string key, string fallbackHex)
        => this.TryFindResource(key, out var v) && v is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));

    private static Button MenuButton(string text, bool destructive, Flyout flyout, Action action)
    {
        var btn = new Button { Content = text };
        btn.Classes.Add("menuItem");
        if (destructive) btn.Classes.Add("destructive");
        btn.Click += (_, _) => { flyout.Hide(); action(); };
        return btn;
    }
}
