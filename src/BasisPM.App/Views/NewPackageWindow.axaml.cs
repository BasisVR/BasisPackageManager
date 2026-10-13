using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class NewPackageWindow : Window
{
    public NewPackageWindow() => InitializeComponent();

    public NewPackageWindow(NewPackageViewModel model) : this()
    {
        DataContext = model;
        model.AskForToken = () => new SignInPromptWindow().ShowDialog<string?>(this);
        Opened += (_, _) => NameBox.Focus();
    }

    private async void OnCreate(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NewPackageViewModel model) return;
        if (await model.ConfirmAsync() is { } draft && IsVisible) Close(draft);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
