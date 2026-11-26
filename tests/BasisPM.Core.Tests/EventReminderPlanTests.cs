using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class EventReminderPlanTests
{
    private static readonly DateTimeOffset Start = new(2026, 12, 12, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Key_is_the_same_for_one_moment_written_in_two_offsets()
    {
        Assert.Equal(EventReminderPlan.Key(Start), EventReminderPlan.Key(Start.ToOffset(TimeSpan.FromHours(10))));
    }

    [Theory]
    [InlineData(-11, false)]
    [InlineData(-10, true)]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(5, false)]
    public void IsDue_only_in_the_ten_minutes_before_the_start(int minutesFromStart, bool due)
    {
        Assert.Equal(due, EventReminderPlan.IsDue(EventReminderPlan.Once(Start), Start, Start.AddMinutes(minutesFromStart)));
    }

    [Fact]
    public void IsDue_is_false_once_the_occurrence_was_notified()
    {
        var reminder = EventReminderPlan.Always();
        reminder.Notified = EventReminderPlan.Key(Start);

        Assert.False(EventReminderPlan.IsDue(reminder, Start, Start.AddMinutes(-5)));
        Assert.True(EventReminderPlan.IsDue(reminder, Start.AddDays(7), Start.AddDays(7).AddMinutes(-5)));
    }

    [Fact]
    public void A_once_reminder_covers_only_its_own_occurrence()
    {
        var reminder = EventReminderPlan.Once(Start);

        Assert.True(EventReminderPlan.AppliesTo(reminder, Start));
        Assert.False(EventReminderPlan.AppliesTo(reminder, Start.AddDays(7)));
        Assert.False(EventReminderPlan.IsSpent(reminder, Start));
        Assert.True(EventReminderPlan.IsSpent(reminder, Start.AddDays(7)));
    }

    [Fact]
    public void A_once_reminder_is_spent_after_it_was_shown()
    {
        var reminder = EventReminderPlan.Once(Start);
        reminder.Notified = EventReminderPlan.Key(Start);

        Assert.True(EventReminderPlan.IsSpent(reminder, Start));
    }

    [Fact]
    public void An_always_reminder_covers_every_occurrence_and_is_never_spent()
    {
        var reminder = EventReminderPlan.Always();
        reminder.Notified = EventReminderPlan.Key(Start);

        Assert.True(EventReminderPlan.AppliesTo(reminder, Start.AddDays(7)));
        Assert.False(EventReminderPlan.IsSpent(reminder, Start));
        Assert.False(EventReminderPlan.IsSpent(reminder, Start.AddDays(7)));
    }
}
