using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class ProjectPartsWindow : Window
{
    public ProjectPartsWindow() => InitializeComponent();

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProjectPartsViewModel { CanApply: true } model) Close(model.Choice);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
