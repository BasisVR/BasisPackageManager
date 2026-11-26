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
    private readonly TimeSpan _dueSlack;
    private List<CommunityEvent> _items = new();
    private Dictionary<string, EventReminder> _reminders = new();
    private bool? _osAllowed;
    private string _shown = "";
    private string? _osSynced;
    private bool _osRegistered;
    private Task _background = Task.CompletedTask;
    private DispatcherTimer? _clock;
    private bool _isLoading;
    private bool _loaded;
    private Task? _loadTask;

    public ObservableCollection<EventCard> Events { get; } = new();
    public RelayCommand RefreshCommand { get; }

    /// <summary>Raised when the user answers the prompt about reminders while the app is closed.</summary>
    public event Action<bool>? OsRemindersAnswered;

    /// <param name="dueSlack">How much earlier than its time a reminder may be shown; a start made by the operating system can land a moment early.</param>
    public EventsViewModel(EventService service, UserSettingsService settings, TimeSpan dueSlack = default)
    {
        _service = service;
        _settings = settings;
        _dueSlack = dueSlack;
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

    /// <summary>Completes when every save and operating system registration started so far has finished.</summary>
    public Task Flush() => _background;

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
            var settings = await _settings.LoadAsync();
            _reminders = settings.EventReminders;
            _osAllowed = settings.OsEventReminders;
            _osRegistered = settings.ScheduledEventReminders.Count > 0;
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
        var os = new List<OsReminder>();
        foreach (var entry in upcoming)
        {
            if (!_reminders.TryGetValue(entry.Item.Id, out var reminder))
            {
                continue;
            }
            if (EventReminderPlan.IsDue(reminder, entry.Start, now + _dueSlack))
            {
                reminder.Notified = EventReminderPlan.Key(entry.Start);
                due.Add(entry);
                changed = true;
            }
            if (EventReminderPlan.IsSpent(reminder, entry.Start))
            {
                _reminders.Remove(entry.Item.Id);
                changed = true;
                continue;
            }
            // The operating system is given the next time this reminder has yet to fire.
            var weekly = reminder.Always && IsWeekly(entry.Item);
            var fireAt = entry.Start - EventReminderPlan.Lead;
            if (weekly && (fireAt <= now || reminder.Notified == EventReminderPlan.Key(entry.Start)))
            {
                fireAt = fireAt.AddDays(7);
            }
            if (fireAt > now)
            {
                os.Add(new OsReminder(entry.Item.Id, fireAt, weekly));
            }
        }
        if (changed)
        {
            SaveReminders();
        }
        // An empty list while reminders exist may only mean the feed could not be read, so registrations are left as they are.
        if (upcoming.Count > 0 || _reminders.Count == 0)
        {
            SyncOs(os);
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
    public async Task SetReminderAsync(EventCard card, EventReminderMode mode)
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

        // The first reminder asks once whether the operating system may show reminders while the app is closed.
        if (mode != EventReminderMode.None && _osAllowed is null && OsReminderScheduler.IsSupported)
        {
            var allowed = await Dialogs.ConfirmAsync(L.Tr("events.dialog.osTitle"), L.Tr("events.dialog.osBody"));
            SetOsReminders(allowed);
            OsRemindersAnswered?.Invoke(allowed);
        }
    }

    /// <summary>Turns reminders through the operating system on or off, saves the choice, and registers or removes them to match.</summary>
    public void SetOsReminders(bool allowed)
    {
        _osAllowed = allowed;
        Queue(() => _settings.Update(s => s.OsEventReminders = allowed), "Saving the event reminder choice");
        Tick(DateTimeOffset.UtcNow);
    }

    private void SyncOs(List<OsReminder> wanted)
    {
        if (!OsReminderScheduler.IsSupported || Environment.ProcessPath is not { } exe)
        {
            return;
        }
        if (_osAllowed != true)
        {
            wanted = new List<OsReminder>();
        }
        var signature = string.Join("|", wanted.Select(w => w.EventId + "=" + OsReminderScheduler.Signature(w, exe)));
        if (signature == _osSynced || (wanted.Count == 0 && !_osRegistered))
        {
            return;
        }
        _osSynced = signature;
        _osRegistered = wanted.Count > 0;
        Queue(() => _settings.Update(s => s.ScheduledEventReminders = OsReminderScheduler.Sync(wanted, s.ScheduledEventReminders, exe)), "Registering event reminders with the operating system");
    }

    private static bool IsWeekly(CommunityEvent item) => string.Equals(item.Repeat?.Trim(), "weekly", StringComparison.OrdinalIgnoreCase);

    private EventReminderMode ModeOf(string id) =>
        !_reminders.TryGetValue(id, out var reminder) ? EventReminderMode.None
        : reminder.Always ? EventReminderMode.Always : EventReminderMode.Once;

    private void SaveReminders()
    {
        var snapshot = _reminders.ToDictionary(p => p.Key, p => new EventReminder { Always = p.Value.Always, Occurrence = p.Value.Occurrence, Notified = p.Value.Notified });
        Queue(() => _settings.Update(s => s.EventReminders = snapshot), "Saving event reminders");
    }

    // Saves and registrations run one after another off the UI thread, in the order they were asked for.
    private void Queue(Action work, string what)
    {
        _background = _background.ContinueWith(_ =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(what, ex);
            }
        }, TaskScheduler.Default);
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

    public void SetReminder(EventReminderMode mode) => _ = _owner.SetReminderAsync(this, mode);

    private static string Day(DateTimeOffset t) => t.ToString("ddd d MMM", CultureInfo.InvariantCulture);
    private static string Time(DateTimeOffset t) => t.ToString("t", CultureInfo.CurrentCulture);

    // The URL comes from the remote feed, so it only ever goes out as a web link.
    private void OpenLink() => ExternalLink.Open(_e.Url);
}
