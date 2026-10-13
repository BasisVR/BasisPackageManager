using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BasisPM.App.ViewModels;
using BasisPM.Core.Services;

namespace BasisPM.App.Views;

public partial class ServerView : UserControl
{
    private ServerConsoleViewModel? _console;
    private readonly ScrollViewer _scroll;
    private bool _stick = true;
    private bool _scrollQueued;

    public ServerView()
    {
        InitializeComponent();
        _scroll = ConsoleScroll;
        _scroll.ScrollChanged += OnScrollChanged;
        _scroll.AddHandler(PointerWheelChangedEvent, OnConsoleWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        _scroll.AddHandler(PointerPressedEvent, OnConsolePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _scroll.AddHandler(KeyDownEvent, OnConsoleListKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        ConsoleInput.AddHandler(KeyDownEvent, OnConsoleKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Watch(null);
        Watch((DataContext as ServerViewModel)?.ServerConsole);
        _stick = true;
        QueueScrollToEnd();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Watch(null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_console is null && DataContext is ServerViewModel vm) Watch(vm.ServerConsole);
        _stick = true;
        QueueScrollToEnd();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var atBottom = _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 4;
        if (atBottom && e.OffsetDelta.Y > 0) _stick = true;
        else if (_stick && !atBottom) QueueScrollToEnd();
    }

    private void OnConsoleWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y > 0) _stick = false;
    }

    private void OnConsolePressed(object? sender, PointerPressedEventArgs e) => _stick = false;

    private void OnConsoleListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.PageUp or Key.Home) _stick = false;
    }

    private void Watch(ServerConsoleViewModel? console)
    {
        if (_console is not null)
        {
            _console.Lines.CollectionChanged -= OnLinesChanged;
            _console.CommandEntered -= OnCommandEntered;
        }
        _console = console;
        if (_console is null) return;
        _console.Lines.CollectionChanged += OnLinesChanged;
        _console.CommandEntered += OnCommandEntered;
    }

    private void OnCommandEntered(string command)
    {
        _stick = true;
        QueueScrollToEnd();
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) _stick = true;
        if (_stick) QueueScrollToEnd();
    }

    private void QueueScrollToEnd()
    {
        if (_scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (!_stick || _console is not { Lines.Count: > 0 } console) return;
            ConsoleList.ScrollIntoView(console.Lines.Count - 1);
            _scroll.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void OnConsoleKeyDown(object? sender, KeyEventArgs e)
    {
        if (_console is null) return;
        switch (e.Key)
        {
            case Key.Enter:
                _console.SendCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Down:
                _console.Recall(e.Key == Key.Up ? -1 : 1);
                ConsoleInput.CaretIndex = ConsoleInput.Text?.Length ?? 0;
                e.Handled = true;
                break;
        }
    }

    private async void OnCopyConsole(object? sender, RoutedEventArgs e)
    {
        if (_console is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(_console.AllText()); }
        catch (Exception ex) { DiagnosticLog.Write("Copying the Basis server console to the clipboard", ex); }
    }
}
