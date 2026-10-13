using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class PackageUpdateWindow : Window
{
    private readonly CancellationTokenSource _loading = new();

    public PackageUpdateWindow() => InitializeComponent();

    public PackageUpdateWindow(PackageUpdateViewModel model) : this()
    {
        DataContext = model;
        Opened += (_, _) => _ = model.LoadAsync(_loading.Token);
        Closed += (_, _) => _loading.Cancel();
    }

    private void OnUpdate(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
