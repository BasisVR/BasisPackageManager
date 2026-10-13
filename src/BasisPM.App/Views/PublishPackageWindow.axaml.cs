using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class PublishPackageWindow : Window
{
    public PublishPackageWindow() => InitializeComponent();

    public PublishPackageWindow(PublishPackageViewModel model) : this()
    {
        DataContext = model;
        model.AskForToken = () => new SignInPromptWindow().ShowDialog<string?>(this);
        Opened += async (_, _) =>
        {
            VersionBox.Focus();
            await model.CheckAsync();
        };
    }

    private async void OnPublish(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PublishPackageViewModel model) return;
        if (await model.ConfirmAsync() is { } draft && IsVisible) Close(draft);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
