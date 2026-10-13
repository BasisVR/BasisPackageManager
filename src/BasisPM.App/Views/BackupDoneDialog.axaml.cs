using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.Localization;
using BasisPM.App.Services;

namespace BasisPM.App.Views;

public partial class BackupDoneDialog : Window
{
    public BackupDoneDialog()
    {
        InitializeComponent();
    }

    public BackupDoneDialog(string project, string size, string zip) : this()
    {
        MessageText.Text = L.Tr("dialog.backupDone.body", project, size);
        PathText.Text = zip;
        QuestionText.Text = L.Tr("dialog.backupDone.question", ExternalLink.FileManagerName);
        ShowText.Text = L.Tr("dialog.backupDone.show", ExternalLink.FileManagerName);
    }

    private void OnShow(object? sender, RoutedEventArgs e) => Close(true);
    private void OnClose(object? sender, RoutedEventArgs e) => Close(false);
}
