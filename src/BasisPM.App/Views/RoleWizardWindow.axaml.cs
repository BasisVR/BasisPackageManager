using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BasisPM.App.Views;

public partial class RoleWizardWindow : Window
{
    public RoleWizardWindow() => InitializeComponent();

    // Result: true = developer (show the Develop tab), false = framework user.
    private void OnDeveloper(object? sender, RoutedEventArgs e) => Close(true);

    private void OnUser(object? sender, RoutedEventArgs e) => Close(false);
}
