using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class BasisChangesWindow : Window
{
    public BasisChangesWindow() => InitializeComponent();

    public BasisChangesWindow(BasisChangesViewModel model) : this()
    {
        DataContext = model;
        model.AskForToken = () => new SignInPromptWindow().ShowDialog<string?>(this);
        Opened += async (_, _) => await model.LoadAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
