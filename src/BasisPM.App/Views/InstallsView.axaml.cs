using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class InstallsView : UserControl
{
    public InstallsView()
    {
        InitializeComponent();
        // Tunnelling, so the name editor sees Enter and Escape before the TextBox does, and a click anywhere else ends the edit.
        AddHandler(KeyDownEvent, OnNameKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPressedOutsideName, RoutingStrategies.Tunnel);
    }

    // A card's name edits in place: Enter or clicking away saves it, Escape puts the old name back.
    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape) || e.Source is not TextBox { DataContext: InstallRow row } box || !box.Classes.Contains("inlineName")) return;
        if (e.Key == Key.Escape) box.SetCurrentValue(TextBox.TextProperty, row.Name);
        e.Handled = true;
        TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
    }

    private void OnPressedOutsideName(object? sender, PointerPressedEventArgs e)
    {
        var focus = TopLevel.GetTopLevel(this)?.FocusManager;
        if (focus?.GetFocusedElement() is TextBox box && box.Classes.Contains("inlineName")
            && e.Source is Visual pressed && pressed != box && !box.IsVisualAncestorOf(pressed))
            focus.ClearFocus();
    }

    private async void OnNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: InstallRow row } box || DataContext is not InstallsViewModel vm) return;
        await vm.RenameAsync(row, box.Text);
        box.SetCurrentValue(TextBox.TextProperty, row.Name);
    }
}
