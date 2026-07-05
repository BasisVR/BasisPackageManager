using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class PackagesView : UserControl
{
    public PackagesView() => InitializeComponent();

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
