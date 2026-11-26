using System.Xml.Linq;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class OsReminderSchedulerTests
{
    private static readonly DateTimeOffset FireAt = new(2026, 12, 12, 19, 50, 0, TimeSpan.Zero);
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void TaskName_keeps_only_safe_characters_of_the_event_id()
    {
        Assert.EndsWith(" weekly-meetup_1", OsReminderScheduler.TaskName("weekly-meetup_1"));
        Assert.EndsWith(" a__b_c", OsReminderScheduler.TaskName("a\\/b\"c"));
    }

    [Fact]
    public void A_once_task_fires_in_utc_and_expires()
    {
        var xml = XDocument.Parse(OsReminderScheduler.TaskXml(new OsReminder("e", FireAt.ToOffset(TimeSpan.FromHours(10)), false), @"C:\Apps\A & B\app.exe").Split('\n', 2)[1]);
        var trigger = xml.Descendants(Ns + "TimeTrigger").Single();

        Assert.Equal("2026-12-12T19:50:00Z", trigger.Element(Ns + "StartBoundary")!.Value);
        Assert.Equal("2026-12-13T19:50:00Z", trigger.Element(Ns + "EndBoundary")!.Value);
        Assert.Null(trigger.Element(Ns + "Repetition"));
        Assert.Single(xml.Descendants(Ns + "DeleteExpiredTaskAfter"));
        Assert.Equal(@"C:\Apps\A & B\app.exe", xml.Descendants(Ns + "Command").Single().Value);
        Assert.Equal(OsReminderScheduler.Argument, xml.Descendants(Ns + "Arguments").Single().Value);
    }

    [Fact]
    public void A_weekly_task_repeats_every_seven_days_and_never_expires()
    {
        var xml = XDocument.Parse(OsReminderScheduler.TaskXml(new OsReminder("e", FireAt, true), "app.exe").Split('\n', 2)[1]);
        var trigger = xml.Descendants(Ns + "TimeTrigger").Single();

        Assert.Equal("P7D", trigger.Element(Ns + "Repetition")!.Element(Ns + "Interval")!.Value);
        Assert.Null(trigger.Element(Ns + "EndBoundary"));
        Assert.Empty(xml.Descendants(Ns + "DeleteExpiredTaskAfter"));
    }

    [Fact]
    public void Signature_changes_with_the_time_the_kind_and_the_program()
    {
        var once = new OsReminder("e", FireAt, false);
        var same = OsReminderScheduler.Signature(once, "app.exe");

        Assert.Equal(same, OsReminderScheduler.Signature(new OsReminder("e", FireAt.ToOffset(TimeSpan.FromHours(2)), false), "app.exe"));
        Assert.NotEqual(same, OsReminderScheduler.Signature(once with { FireAt = FireAt.AddHours(1) }, "app.exe"));
        Assert.NotEqual(same, OsReminderScheduler.Signature(once with { Weekly = true }, "app.exe"));
        Assert.NotEqual(same, OsReminderScheduler.Signature(once, "other.exe"));
    }
}
