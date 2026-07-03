using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace BasisPM.App.Views;

public partial class AliasPromptWindow : Window
{
    public AliasPromptWindow() => InitializeComponent();

    public AliasPromptWindow(string title, string path, string suggested) : this()
    {
        TitleText.Text = title;
        PathText.Text = path;
        Input.Text = suggested;
        Opened += (_, _) => { Input.Focus(); Input.SelectAll(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) OnOk(this, new RoutedEventArgs());
            else if (e.Key == Key.Escape) Close(null);
        };
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var v = Input.Text?.Trim();
        Close(string.IsNullOrWhiteSpace(v) ? null : v);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
