using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BasisPM.App.Views;

public enum BackupChoice { Cancel, Continue, Backup }

public partial class BackupPromptDialog : Window
{
    public BackupPromptDialog()
    {
        InitializeComponent();
    }

    public BackupPromptDialog(string message) : this()
    {
        MessageText.Text = message;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(BackupChoice.Cancel);
    private void OnContinue(object? sender, RoutedEventArgs e) => Close(BackupChoice.Continue);
    private void OnBackup(object? sender, RoutedEventArgs e) => Close(BackupChoice.Backup);
}
