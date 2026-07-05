using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class PackagesView : UserControl
{
    public PackagesView() => InitializeComponent();

    // The "Manage" menu for an installed package is built in code and its commands invoked
    // directly, so it doesn't rely on binding resolution inside the flyout popup.
    private void OnManageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not PackageRow row) return;
        if (DataContext is not PackagesViewModel vm) return;

        var flyout = new MenuFlyout();

        var update = new MenuItem { Header = L.Tr("packages.button.update") };
        update.Click += (_, _) => vm.InstallCommand.Execute(row.Entry);
        flyout.Items.Add(update);

        if (row.HasGit)
        {
            var choose = new MenuItem { Header = L.Tr("packages.button.chooseVersion") };
            choose.Click += (_, _) => vm.ChooseVersionCommand.Execute(row.Entry);
            flyout.Items.Add(choose);
        }

        flyout.Items.Add(new Separator());

        var remove = new MenuItem { Header = L.Tr("packages.button.remove") };
        remove.Click += (_, _) => vm.RemoveCommand.Execute(row.Entry);
        flyout.Items.Add(remove);

        flyout.ShowAt(control);
    }
}
