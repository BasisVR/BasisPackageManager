using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class BranchPickerWindow : Window
{
    private BranchChoice? _selected;

    public BranchPickerWindow() => InitializeComponent();

    public BranchPickerWindow(string title, IReadOnlyList<string> branches, string current)
        : this(title, L.Tr("dialog.branchPicker.sub", current), new[]
        {
            new BranchGroup("", "", branches.Select(b => new BranchChoice(b, b, null, true, string.Equals(b, current, StringComparison.OrdinalIgnoreCase))).ToList()),
        })
    {
    }

    public BranchPickerWindow(string title, string subtitle, IReadOnlyList<BranchGroup> groups) : this()
    {
        TitleText.Text = title;
        SubText.Text = subtitle;
        Groups.ItemsSource = groups;
        _selected = groups.SelectMany(g => g.Items).FirstOrDefault(i => i.IsCurrent) ?? groups.SelectMany(g => g.Items).FirstOrDefault();
        Groups.AddHandler(SelectingItemsControl.SelectionChangedEvent, OnSelectionChanged);
        Groups.AddHandler(DoubleTappedEvent, (_, _) => { if (_selected is not null) Close(_selected); });
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            var list = Lists().FirstOrDefault(l => l.Items.Contains(_selected));
            if (list is not null) list.SelectedItem = _selected;
        });
    }

    public BranchChoice? Selected => _selected;

    private IEnumerable<ListBox> Lists() => Groups.GetVisualDescendants().OfType<ListBox>();

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not ListBox source || source.SelectedItem is not BranchChoice choice) return;
        _selected = choice;
        foreach (var list in Lists())
            if (!ReferenceEquals(list, source)) list.SelectedItem = null;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(_selected);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
