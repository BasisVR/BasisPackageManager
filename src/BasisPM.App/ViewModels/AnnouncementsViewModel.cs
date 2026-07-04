using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using BasisPM.App.Localization;
using BasisPM.App.Services;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

/// <summary>
/// The Announcements page — shows project news fetched from the static website
/// (basisvr.org/announcements.json), newest first, with an offline fallback.
/// </summary>
public sealed class AnnouncementsViewModel : ObservableObject
{
    private readonly AnnouncementService _service;
    private bool _isLoading;
    private bool _loaded;

    public ObservableCollection<AnnouncementCard> Announcements { get; } = new();
    public RelayCommand RefreshCommand { get; }

    public AnnouncementsViewModel(AnnouncementService service)
    {
        _service = service;
        RefreshCommand = new RelayCommand(() => LoadAsync(force: true));
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set { if (SetField(ref _isLoading, value)) OnPropertyChanged(nameof(ShowEmpty)); }
    }

    /// <summary>Shown when a completed load produced nothing (rare — the embedded fallback normally fills it).</summary>
    public bool ShowEmpty => _loaded && !IsLoading && Announcements.Count == 0;

    /// <summary>Ids of everything currently shown — used by the shell to track which are unread.</summary>
    public IReadOnlyList<string> AllIds =>
        Announcements.Select(a => a.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();

    public async Task LoadAsync(bool force = false)
    {
        if (_loaded && !force) return;
        IsLoading = true;
        try
        {
            var items = await _service.LoadAsync();
            Announcements.Clear();
            foreach (var a in items)
                Announcements.Add(new AnnouncementCard(a));
            _loaded = true;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmpty));
        }
    }
}

/// <summary>Display wrapper around a single <see cref="Announcement"/>.</summary>
public sealed class AnnouncementCard
{
    private readonly Announcement _a;

    public AnnouncementCard(Announcement a)
    {
        _a = a;
        OpenLinkCommand = new RelayCommand(OpenLink);
    }

    public string Id => _a.Id;
    public string Title => _a.Title;
    public string Body => _a.Body;
    public bool IsPinned => _a.Pinned;

    public bool HasDate => !string.IsNullOrWhiteSpace(_a.Date);

    public string DateDisplay =>
        DateTimeOffset.TryParse(_a.Date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : _a.Date ?? "";

    public string LevelLabel => Norm switch
    {
        "update" => L.Tr("announcements.level.update"),
        "alert" => L.Tr("announcements.level.alert"),
        _ => L.Tr("announcements.level.news"),
    };

    public IBrush LevelBrush => new SolidColorBrush(Color.Parse(Norm switch
    {
        "update" => "#9333EA",
        "alert" => "#EF1237",
        _ => "#3B82F6",
    }));

    public bool HasLink => !string.IsNullOrWhiteSpace(_a.Url);

    public string LinkText =>
        (string.IsNullOrWhiteSpace(_a.LinkText) ? L.Tr("announcements.card.learnMore") : _a.LinkText!) + "  ↗";

    public RelayCommand OpenLinkCommand { get; }

    private string Norm => (_a.Level ?? "").Trim().ToLowerInvariant();

    // The URL comes from the remote announcements feed, so only ever hand a real web link to the OS
    // shell — never a local file/UNC/protocol handler it would launch as a program.
    private void OpenLink() => ExternalLink.Open(_a.Url);
}
