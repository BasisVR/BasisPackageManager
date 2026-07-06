using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.Localization;

namespace BasisPM.App.Views;

public partial class BranchPickerWindow : Window
{
    public BranchPickerWindow() => InitializeComponent();

    public BranchPickerWindow(string title, IReadOnlyList<string> branches, string current) : this()
    {
        TitleText.Text = title;
        SubText.Text = L.Tr("dialog.branchPicker.sub", current);
        List.ItemsSource = branches;
        List.SelectedItem = branches.FirstOrDefault(b => string.Equals(b, current, StringComparison.OrdinalIgnoreCase))
                            ?? branches.FirstOrDefault();
        List.DoubleTapped += (_, _) => { if (List.SelectedItem is string s) Close(s); };
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(List.SelectedItem as string);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
