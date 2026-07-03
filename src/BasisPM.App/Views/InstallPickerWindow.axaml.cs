using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.Core.Models;

namespace BasisPM.App.Views;

public partial class InstallPickerWindow : Window
{
    public InstallPickerWindow() => InitializeComponent();

    public InstallPickerWindow(string title, IReadOnlyList<BasisInstall> installs) : this()
    {
        TitleText.Text = title;
        List.ItemsSource = installs;
        if (installs.Count > 0) List.SelectedIndex = 0;
        List.DoubleTapped += (_, _) => Close(List.SelectedItem as BasisInstall);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(List.SelectedItem as BasisInstall);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
