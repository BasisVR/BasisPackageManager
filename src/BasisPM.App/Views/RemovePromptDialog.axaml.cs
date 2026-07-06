using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.Localization;

namespace BasisPM.App.Views;

public enum RemoveChoice { Cancel, RemoveFromList, DeleteFromDisk }

public partial class RemovePromptDialog : Window
{
    public RemovePromptDialog()
    {
        InitializeComponent();
    }

    public RemovePromptDialog(string projectName, string path) : this()
    {
        HeadingText.Text = L.Tr("dialog.remove.heading", projectName);
        PathText.Text = path;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(RemoveChoice.Cancel);
    private void OnRemoveFromList(object? sender, RoutedEventArgs e) => Close(RemoveChoice.RemoveFromList);
    private void OnDeleteFromDisk(object? sender, RoutedEventArgs e) => Close(RemoveChoice.DeleteFromDisk);
}
