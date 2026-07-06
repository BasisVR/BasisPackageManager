using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace BasisPM.App.Views;

public partial class SignInPromptWindow : Window
{
    public SignInPromptWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Input.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) OnOk(this, new RoutedEventArgs());
            else if (e.Key == Key.Escape) Close(null);
        };
    }

    // Returns the trimmed token, or null when blank/cancelled.
    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var v = Input.Text?.Trim();
        Close(string.IsNullOrWhiteSpace(v) ? null : v);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
