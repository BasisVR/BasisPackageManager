using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using BasisPM.App.Localization;
using BasisPM.App.Services;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

/// <summary>
/// The Home page's Events section: upcoming community events fetched from the static website
/// (basisvr.org/events.json), soonest first, with an offline fallback, and the reminders set on them.
/// </summary>
public sealed class EventsViewModel : ObservableObject
{
    /// <summary>How long before its start an event is marked as starting soon.</summary>
    public static readonly TimeSpan SoonWindow = TimeSpan.FromHours(1);

    private readonly EventService _service;
    private readonly UserSettingsService _settings;
    private List<CommunityEvent> _items = new();
    private Dictionary<string, EventReminder> _reminders = new();
    private string _shown = "";
    private DispatcherTimer? _clock;
    private bool _isLoading;
    private bool _loaded;
    private Task? _loadTask;

    public ObservableCollection<EventCard> Events { get; } = new();
    public RelayCommand RefreshCommand { get; }

    public EventsViewModel(EventService service, UserSettingsService settings)
    {
        _service = service;
        _settings = settings;
        RefreshCommand = new RelayCommand(() => LoadAsync(force: true));
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetField(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(ShowEmpty));
            }
        }
    }

    public bool ShowEmpty => _loaded && !IsLoading && Events.Count == 0;

    // Concurrent callers share the one in-flight fetch.
    public Task LoadAsync(bool force = false)
    {
        if (force)
        {
            _loaded = false;
            _loadTask = null;
        }
        if (_loaded)
        {
            return Task.CompletedTask;
        }
        return _loadTask ??= LoadCoreAsync();
    }

    private async Task LoadCoreAsync()
    {
        IsLoading = true;
        try
        {
            _reminders = (await _settings.LoadAsync()).EventReminders;
            _items = await _service.LoadAsync();
            _shown = "";
            Tick(DateTimeOffset.UtcNow);
            _loaded = true;
            StartClock();
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmpty));
            _loadTask = null;
        }
    }

    /// <summary>
    /// Brings the section up to <paramref name="now"/>: finished events drop off, weekly ones roll on, the soon and
    /// live marks follow the time, and any reminder that has come due is shown.
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        var upcoming = new List<(CommunityEvent Item, DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var item in EventService.Upcoming(_items, now))
        {
            if (EventService.ParseTime(item.Start) is { } start)
            {
                upcoming.Add((item, start, EventService.EndOf(item, start)));
            }
        }

        var changed = false;
        var due = new List<(CommunityEvent Item, DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var entry in upcoming)
        {
            if (!_reminders.TryGetValue(entry.Item.Id, out var reminder))
            {
                continue;
            }
            if (EventReminderPlan.IsDue(reminder, entry.Start, now))
            {
                reminder.Notified = EventReminderPlan.Key(entry.Start);
                due.Add(entry);
                changed = true;
            }
            if (EventReminderPlan.IsSpent(reminder, entry.Start))
            {
                _reminders.Remove(entry.Item.Id);
                changed = true;
            }
        }
        // A reminder whose event has left the feed is dropped, but only when the feed has events: an empty list may just mean offline.
        if (upcoming.Count > 0)
        {
            foreach (var id in _reminders.Keys.Where(id => upcoming.All(u => u.Item.Id != id)).ToList())
            {
                _reminders.Remove(id);
                changed = true;
            }
        }
        if (changed)
        {
            SaveReminders();
        }

        // Cards are only rebuilt when something on them would differ, so an open menu or tooltip is left alone.
        var cards = upcoming.Select(u => new EventCard(this, u.Item, u.Start, u.End, now, ModeOf(u.Item.Id))).ToList();
        var signature = string.Join("|", cards.Select(c => c.Signature));
        if (signature != _shown)
        {
            _shown = signature;
            Events.Clear();
            foreach (var card in cards)
            {
                Events.Add(card);
            }
            OnPropertyChanged(nameof(ShowEmpty));
        }

        foreach (var entry in due)
        {
            Dialogs.ShowEventReminder(new EventCard(this, entry.Item, entry.Start, entry.End, now, EventReminderMode.None));
        }
    }

    /// <summary>Sets, changes or clears the reminder on <paramref name="card"/>'s event and saves it.</summary>
    public void SetReminder(EventCard card, EventReminderMode mode)
    {
        if (mode == EventReminderMode.None)
        {
            _reminders.Remove(card.Id);
        }
        else
        {
            _reminders[card.Id] = mode == EventReminderMode.Always ? EventReminderPlan.Always() : EventReminderPlan.Once(card.StartsAt);
        }
        SaveReminders();
        Tick(DateTimeOffset.UtcNow);
    }

    private EventReminderMode ModeOf(string id) =>
        !_reminders.TryGetValue(id, out var reminder) ? EventReminderMode.None
        : reminder.Always ? EventReminderMode.Always : EventReminderMode.Once;

    private void SaveReminders()
    {
        var snapshot = _reminders.ToDictionary(p => p.Key, p => new EventReminder { Always = p.Value.Always, Occurrence = p.Value.Occurrence, Notified = p.Value.Notified });
        _ = Task.Run(() =>
        {
            try
            {
                _settings.Update(s => s.EventReminders = snapshot);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("Saving event reminders", ex);
            }
        });
    }

    // The marks and reminders depend on the time of day, so the section is brought up to date a few times a minute.
    private void StartClock()
    {
        if (_clock is not null)
        {
            return;
        }
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _clock.Tick += (_, _) => Tick(DateTimeOffset.UtcNow);
        _clock.Start();
    }
}

public enum EventReminderMode { None, Once, Always }

/// <summary>Display wrapper around a single <see cref="CommunityEvent"/>, with its times in the viewer's local time.</summary>
public sealed class EventCard
{
    private readonly EventsViewModel _owner;
    private readonly CommunityEvent _e;
    private readonly DateTimeOffset _start;
    private readonly DateTimeOffset _end;

    public EventCard(EventsViewModel owner, CommunityEvent e, DateTimeOffset start, DateTimeOffset end, DateTimeOffset now, EventReminderMode reminder)
    {
        _owner = owner;
        _e = e;
        StartsAt = start;
        _start = start.ToLocalTime();
        _end = end.ToLocalTime();
        IsLive = start <= now && now < end;
        IsSoon = now < start && start - now <= EventsViewModel.SoonWindow;
        Reminder = reminder;
        OpenLinkCommand = new RelayCommand(OpenLink);
        ToggleReminderCommand = new RelayCommand(() => SetReminder(HasReminder ? EventReminderMode.None : EventReminderMode.Once));
    }

    public string Id => _e.Id;
    public DateTimeOffset StartsAt { get; }

    public string Title => _e.Title;
    public string Description => _e.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(_e.Description);

    public string MonthLabel => _start.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant();
    public string DayLabel => _start.Day.ToString(CultureInfo.InvariantCulture);

    /// <summary>"Mon 14 Dec, 19:00 to 21:00" in local time; the end is left off when the feed gave none.</summary>
    public string TimeLabel
    {
        get
        {
            var text = Day(_start) + ", " + Time(_start);
            if (EventService.ParseTime(_e.End) is null)
            {
                return text;
            }
            return _end.Date == _start.Date ? text + " – " + Time(_end) : text + " – " + Day(_end) + ", " + Time(_end);
        }
    }

    /// <summary>Underway right now.</summary>
    public bool IsLive { get; }

    /// <summary>Not started yet, but starting within <see cref="EventsViewModel.SoonWindow"/>.</summary>
    public bool IsSoon { get; }

    public bool IsRepeating => string.Equals(_e.Repeat?.Trim(), "weekly", StringComparison.OrdinalIgnoreCase);

    public EventReminderMode Reminder { get; }
    public bool HasReminder => Reminder != EventReminderMode.None;
    public bool IsReminderOnce => Reminder == EventReminderMode.Once;
    public bool IsReminderAlways => Reminder == EventReminderMode.Always;

    // A reminder is for before the start, so an event already underway has none to offer.
    public bool ShowReminderToggle => !IsLive && !IsRepeating;
    public bool ShowReminderMenu => !IsLive && IsRepeating;

    public string ReminderLabel => Reminder switch
    {
        EventReminderMode.Always => L.Tr("events.button.reminderAlways"),
        EventReminderMode.Once => L.Tr("events.button.reminderOnce"),
        _ => L.Tr("events.button.remind"),
    };

    public bool HasHost => !string.IsNullOrWhiteSpace(_e.Host);
    public string HostLabel => L.Tr("community.events.hostedBy", _e.Host ?? "");

    public bool HasLocation => !string.IsNullOrWhiteSpace(_e.Location);
    public string Location => _e.Location ?? "";

    public bool HasLink => !string.IsNullOrWhiteSpace(_e.Url);
    public string LinkText => (string.IsNullOrWhiteSpace(_e.LinkText) ? L.Tr("events.card.details") : _e.LinkText!) + "  ↗";

    public RelayCommand OpenLinkCommand { get; }
    public RelayCommand ToggleReminderCommand { get; }

    /// <summary>Everything the card shows that can change between two ticks.</summary>
    public string Signature => string.Join("~", Id, EventReminderPlan.Key(StartsAt), _e.Title, _e.End, IsLive, IsSoon, Reminder);

    public void SetReminder(EventReminderMode mode) => _owner.SetReminder(this, mode);

    private static string Day(DateTimeOffset t) => t.ToString("ddd d MMM", CultureInfo.InvariantCulture);
    private static string Time(DateTimeOffset t) => t.ToString("t", CultureInfo.CurrentCulture);

    // The URL comes from the remote feed, so it only ever goes out as a web link.
    private void OpenLink() => ExternalLink.Open(_e.Url);
}
