using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class SubmitPrWindow : Window
{
    public SubmitPrWindow() => InitializeComponent();

    public SubmitPrWindow(SubmitPrViewModel model) : this()
    {
        DataContext = model;
        model.AskForToken = () => new SignInPromptWindow().ShowDialog<string?>(this);
        Opened += (_, _) =>
        {
            model.SelectFile(model.Files.FirstOrDefault());
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    private async void OnOk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SubmitPrViewModel model) return;
        if (string.IsNullOrWhiteSpace(model.Title)) { TitleBox.Focus(); return; }
        if (await model.SignInAsync() is { } draft && IsVisible) Close(draft);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
