using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class EventReminderWindow : Window
{
    private const int EdgeGap = 16;

    public EventReminderWindow()
    {
        InitializeComponent();
        Opened += (_, _) => MoveToCorner();
    }

    // Bottom right of the main screen's work area, clear of the taskbar.
    private void MoveToCorner()
    {
        if (Screens.Primary is not { } screen)
        {
            return;
        }
        var scale = screen.Scaling;
        var size = FrameSize ?? ClientSize;
        var area = screen.WorkingArea;
        Position = new PixelPoint(
            area.Right - (int)Math.Ceiling((size.Width + EdgeGap) * scale),
            area.Bottom - (int)Math.Ceiling((size.Height + EdgeGap) * scale));
    }

    private void OnDismiss(object? sender, RoutedEventArgs e) => Close();

    private void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EventCard card)
        {
            card.OpenLinkCommand.Execute(null);
        }
        Close();
    }
}
