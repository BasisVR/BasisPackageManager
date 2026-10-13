using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Services;

namespace BasisPM.App.Views;

public partial class LicensesWindow : Window
{
    public LicensesWindow() => InitializeComponent();

    private LicensesViewModel? Licenses => DataContext as LicensesViewModel;

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Licenses is not { } licenses || Clipboard is not { } clipboard) return;
        try
        {
            await clipboard.SetTextAsync(licenses.Markdown);
            licenses.ReportCopied();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Copying the third-party notices", ex);
            licenses.ReportError(L.Tr("dialog.licenses.copyFailed", ex.Message));
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (Licenses is not { } licenses) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L.Tr("dialog.licenses.picker"),
                SuggestedFileName = LicensesViewModel.NoticesFileName,
                DefaultExtension = "md",
                FileTypeChoices = new[] { new FilePickerFileType(L.Tr("dialog.licenses.fileType")) { Patterns = new[] { "*.md", "*.txt" }, MimeTypes = new[] { "text/markdown", "text/plain" } } },
            });
            if (file?.TryGetLocalPath() is not { Length: > 0 } path) return;
            await File.WriteAllTextAsync(path, licenses.Markdown);
            licenses.ReportSaved(path);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Saving the third-party notices", ex);
            licenses.ReportError(L.Tr("dialog.licenses.saveFailed", ex.Message));
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
