using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

        var panel = new StackPanel { Spacing = 2, MinWidth = 168 };
        var flyout = new Flyout { Content = panel };

        panel.Children.Add(MenuButton(L.Tr("packages.button.update"), false, flyout, () => vm.InstallCommand.Execute(row.Entry)));
        if (row.HasGit)
            panel.Children.Add(MenuButton(L.Tr("packages.button.chooseVersion"), false, flyout, () => vm.ChooseVersionCommand.Execute(row.Entry)));
        panel.Children.Add(MenuButton(L.Tr("packages.button.remove"), true, flyout, () => vm.RemoveCommand.Execute(row.Entry)));

        flyout.ShowAt(control);
    }

    private static Button MenuButton(string text, bool destructive, Flyout flyout, Action action)
    {
        var btn = new Button { Content = text };
        btn.Classes.Add("menuItem");
        if (destructive) btn.Classes.Add("destructive");
        btn.Click += (_, _) => { flyout.Hide(); action(); };
        return btn;
    }
}
