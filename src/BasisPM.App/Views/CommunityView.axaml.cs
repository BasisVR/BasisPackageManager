using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class CommunityView : UserControl
{
    public CommunityView()
    {
        InitializeComponent();
    }

    // One of the reminder menu's choices: close the menu, then apply the choice named by the button's Tag.
    private void OnReminderChoice(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EventCard card, Tag: string tag } button || !Enum.TryParse<EventReminderMode>(tag, out var mode))
        {
            return;
        }
        (button.FindLogicalAncestorOfType<FlyoutPresenter>()?.Parent as Popup)?.Close();
        card.SetReminder(mode);
    }
}
