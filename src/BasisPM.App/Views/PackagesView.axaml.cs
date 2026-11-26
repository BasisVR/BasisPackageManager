using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class PackagesView : UserControl
{
    public PackagesView() => InitializeComponent();

    // Clicking a package row opens its detail panel — but a tap that landed on one of the row's
    // buttons (Install / Uninstall) should run that button, not open the panel.
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
}
