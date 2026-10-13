using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class ProjectStorageWindow : Window
{
    public ProjectStorageWindow() => InitializeComponent();

    private ProjectStorageViewModel? Storage => DataContext as ProjectStorageViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Storage is { Report: null, IsMeasuring: false } storage) _ = storage.MeasureAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        Storage?.Stop();
        base.OnClosed(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
