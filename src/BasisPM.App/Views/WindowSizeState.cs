using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.Views;

public sealed class WindowSizeState
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);
    private readonly Window _window;
    private readonly UserSettingsService? _settings;
    private readonly DispatcherTimer _settle = new(DispatcherPriority.Background) { Interval = SettleDelay };
    private Bounds _current;
    private Bounds _saved;

    private WindowSizeState(Window window, UserSettingsService? settings, UserSettings saved)
    {
        _window = window;
        _settings = settings;
        _current = _saved = new Bounds(saved.WindowWidth ?? 0, saved.WindowHeight ?? 0, saved.WindowMaximized);
        if (saved.WindowWidth is double width && saved.WindowHeight is double height && Usable(width) && Usable(height))
        {
            if ((window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary) is { } screen)
            {
                var area = screen.WorkingArea.ToRect(screen.Scaling);
                var frame = window.FrameSize ?? window.ClientSize;
                var chromeWidth = Math.Max(0, frame.Width - window.ClientSize.Width);
                var chromeHeight = Math.Max(0, frame.Height - window.ClientSize.Height);
                width = Math.Min(width, area.Width - chromeWidth);
                height = Math.Min(height, area.Height - chromeHeight);
                var origin = window.Position.ToPoint(screen.Scaling);
                var x = Math.Max(area.X, Math.Min(origin.X, area.Right - width - chromeWidth));
                var y = Math.Max(area.Y, Math.Min(origin.Y, area.Bottom - height - chromeHeight));
                if (x != origin.X || y != origin.Y) window.Position = PixelPoint.FromPoint(new Point(x, y), screen.Scaling);
            }
            window.Width = width;
            window.Height = height;
        }
        if (saved.WindowMaximized) window.WindowState = WindowState.Maximized;
        _settle.Tick += (_, _) => _ = SettleAsync();
        window.PropertyChanged += OnWindowPropertyChanged;
        window.Closing += (_, _) => Flush();
        window.Closed += (_, _) =>
        {
            window.PropertyChanged -= OnWindowPropertyChanged;
            _settle.Stop();
        };
    }

    public static void Attach(Window window, UserSettingsService? settings, UserSettings saved) => _ = new WindowSizeState(window, settings, saved);

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TopLevel.ClientSizeProperty && e.Property != Window.WindowStateProperty) return;
        _settle.Stop();
        _settle.Start();
    }

    private void Capture()
    {
        if (_window.WindowState == WindowState.Maximized) _current = _current with { Maximized = true };
        else if (_window.WindowState == WindowState.Normal && _window.ClientSize is { Width: > 0, Height: > 0 } size)
            _current = new Bounds(Math.Round(size.Width), Math.Round(size.Height), false);
    }

    private async Task SettleAsync()
    {
        _settle.Stop();
        Capture();
        if (_settings is not { } settings || _current == _saved) return;
        var bounds = _saved = _current;
        try { await settings.UpdateAsync(s => Apply(s, bounds)); }
        catch (Exception ex) { _saved = default; DiagnosticLog.Write("Saving the window size", ex); }
    }

    private void Flush()
    {
        _settle.Stop();
        Capture();
        if (_settings is not { } settings || _current == _saved) return;
        var bounds = _current;
        try { if (Task.Run(() => settings.Update(s => Apply(s, bounds))).Wait(FlushTimeout)) _saved = bounds; }
        catch (Exception ex) { DiagnosticLog.Write("Saving the window size", ex); }
    }

    private static void Apply(UserSettings settings, Bounds bounds)
    {
        settings.WindowWidth = Usable(bounds.Width) ? bounds.Width : null;
        settings.WindowHeight = Usable(bounds.Height) ? bounds.Height : null;
        settings.WindowMaximized = bounds.Maximized;
    }

    private static bool Usable(double value) => value is > 0 and < double.PositiveInfinity;

    private readonly record struct Bounds(double Width, double Height, bool Maximized);
}
