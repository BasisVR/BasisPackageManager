using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class CloneBasisWindow : Window
{
    public CloneBasisWindow() => InitializeComponent();

    private void OnClone(object? sender, RoutedEventArgs e) => Close((DataContext as CloneBasisViewModel)?.LeaveOut);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
