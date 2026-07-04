using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.Localization;
using BasisPM.Core.Services;

namespace BasisPM.App.Views;

public partial class SubmitPrWindow : Window
{
    public SubmitPrWindow() => InitializeComponent();

    public SubmitPrWindow(string packageId, string suggestedBranch) : this()
    {
        SubText.Text = L.Tr("dialog.submitPr.contributeHint", packageId);
        TitleBox.Text = L.Tr("dialog.submitPr.defaultTitle", packageId);
        BranchBox.Text = suggestedBranch;
        Opened += (_, _) => { TitleBox.Focus(); TitleBox.SelectAll(); };
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title)) { TitleBox.Focus(); return; }
        var branch = string.IsNullOrWhiteSpace(BranchBox.Text) ? "basis-pm-edit" : BranchBox.Text.Trim();
        var body = string.IsNullOrWhiteSpace(BodyBox.Text) ? null : BodyBox.Text.Trim();
        Close(new PrRequest(title!, body, branch, null));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
