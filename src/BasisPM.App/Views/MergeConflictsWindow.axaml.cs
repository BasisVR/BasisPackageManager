using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.Views;

public partial class MergeConflictsWindow : Window
{
    public MergeConflictsWindow() => InitializeComponent();

    public MergeConflictsWindow(MergeConflictsViewModel model) : this()
    {
        DataContext = model;
        model.Completed += OnCompleted;
        Closed += (_, _) => model.Completed -= OnCompleted;
    }

    private MergeConflictsViewModel? Model => DataContext as MergeConflictsViewModel;

    private void OnCompleted(BasisUpdateResult result) => Close(result);

    private void OnLater(object? sender, RoutedEventArgs e) => Close(null);

    private async void OnAbort(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        try
        {
            var confirmed = await new ConfirmWindow(L.Tr("dialog.conflicts.confirmAbortTitle"), L.Tr("dialog.conflicts.confirmAbortBody"))
                .ShowDialog<bool>(this);
            if (confirmed) await model.AbortAsync();
        }
        catch (Exception ex) { DiagnosticLog.Write("Undoing a Basis update from the conflicts dialog", ex); }
    }
}
