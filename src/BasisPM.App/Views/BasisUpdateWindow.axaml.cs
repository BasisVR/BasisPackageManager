using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Services;

namespace BasisPM.App.Views;

public partial class BasisUpdateWindow : Window
{
    public BasisUpdateWindow() => InitializeComponent();

    public BasisUpdateWindow(BasisUpdateReviewViewModel model) : this() => DataContext = model;

    private BasisUpdateReviewViewModel? Model => DataContext as BasisUpdateReviewViewModel;

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(BasisUpdateDecision.Cancel);

    private void OnPrimary(object? sender, RoutedEventArgs e)
    {
        if (Model is { HasPrimary: true, IsWorking: false } model) Close(model.PrimaryDecision);
    }

    private async void OnChangeBranch(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        try
        {
            var branches = await model.ListBranchesAsync();
            if (branches.Count == 0) return;
            var picked = await new BranchPickerWindow(L.Tr("dialog.basisUpdate.pickBranchTitle"), branches, model.BasisBranch).ShowDialog<string?>(this);
            if (!string.IsNullOrWhiteSpace(picked)) await model.SwitchBranchAsync(picked);
        }
        catch (Exception ex) { DiagnosticLog.Write("Changing the Basis branch in the update dialog", ex); }
    }
}
