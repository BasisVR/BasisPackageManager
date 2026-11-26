using Avalonia;
using Avalonia.Controls;

namespace BasisPM.App.Views;

/// <summary>
/// The app's animated brand background. Its animations only run while the window it sits in is in front and not
/// minimized, so a window left in the background stops repainting.
/// </summary>
public partial class AnimatedBackdrop : UserControl
{
    private Window? _window;

    public AnimatedBackdrop() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is null)
        {
            return;
        }
        _window.Activated += OnForegroundChanged;
        _window.Deactivated += OnForegroundChanged;
        _window.PropertyChanged += OnWindowPropertyChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null)
        {
            _window.Activated -= OnForegroundChanged;
            _window.Deactivated -= OnForegroundChanged;
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            Refresh();
        }
    }

    private void OnForegroundChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
        => BgRoot.Classes.Set("bgLive", _window is { IsActive: true } window && window.WindowState != WindowState.Minimized);
}
