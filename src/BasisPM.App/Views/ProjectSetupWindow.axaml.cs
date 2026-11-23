using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BasisPM.App.Views;

public partial class ProjectSetupWindow : Window
{
    public ProjectSetupWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ViewModels.ProjectSetupViewModel model) model.CloseRequested += Close;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
