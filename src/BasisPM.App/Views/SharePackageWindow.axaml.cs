using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class SharePackageWindow : Window
{
    public SharePackageWindow() => InitializeComponent();

    public SharePackageWindow(SharePackageViewModel model) : this()
    {
        DataContext = model;
        model.AskForToken = () => new SignInPromptWindow().ShowDialog<string?>(this);
        Opened += (_, _) =>
        {
            if (model.NeedsMessage) CommitMessageBox.Focus();
            else BranchBox.Focus();
        };
    }

    private async void OnShare(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SharePackageViewModel model) return;
        if (await model.SignInAsync() is { } draft && IsVisible) Close(draft);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
