using System.Globalization;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

/// <summary>Decides when a saved <see cref="EventReminder"/> applies to an occurrence of its event and when it is due.</summary>
public static class EventReminderPlan
{
    /// <summary>How long before an event starts its reminder is shown.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(10);

    /// <summary>The saved form of an occurrence's start, the same whatever offset the feed wrote it in.</summary>
    public static string Key(DateTimeOffset start) => start.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>A reminder for every occurrence of a repeating event.</summary>
    public static EventReminder Always() => new() { Always = true };

    /// <summary>A reminder for the one occurrence starting at <paramref name="start"/>.</summary>
    public static EventReminder Once(DateTimeOffset start) => new() { Occurrence = Key(start) };

    public static bool AppliesTo(EventReminder reminder, DateTimeOffset start) => reminder.Always || reminder.Occurrence == Key(start);

    /// <summary>True from <see cref="Lead"/> before the start until the start, once per occurrence.</summary>
    public static bool IsDue(EventReminder reminder, DateTimeOffset start, DateTimeOffset now) =>
        AppliesTo(reminder, start) && reminder.Notified != Key(start) && now >= start - Lead && now < start;

    /// <summary>A just-once reminder is spent when it has been shown or its occurrence is no longer the next one.</summary>
    public static bool IsSpent(EventReminder reminder, DateTimeOffset start) =>
        !reminder.Always && (reminder.Occurrence != Key(start) || reminder.Notified == Key(start));
}
