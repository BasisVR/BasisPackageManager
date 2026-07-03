using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BasisPM.App.Views;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
    }

    public ConfirmWindow(string title, string message) : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
    }

    private void OnYes(object? sender, RoutedEventArgs e) => Close(true);
    private void OnNo(object? sender, RoutedEventArgs e) => Close(false);
}
