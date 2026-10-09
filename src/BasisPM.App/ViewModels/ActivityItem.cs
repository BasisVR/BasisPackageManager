using System.Text.RegularExpressions;
using BasisPM.App.Localization;

namespace BasisPM.App.ViewModels;

public sealed class ActivityItem : ObservableObject
{
    private string _detail = "";
    private double _progress;
    private bool _isIndeterminate = true;

    public ActivityItem(string title, string? project)
    {
        Title = title;
        Project = string.IsNullOrWhiteSpace(project) ? null : project.Trim();
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; }
    public string? Project { get; }
    public bool HasProject => Project is not null;
    public DateTimeOffset Started { get; } = DateTimeOffset.Now;
    public string StartedLabel => L.Tr("settings.activity.started", Started.LocalDateTime.ToString("t"));
    public string Detail { get => _detail; private set { if (SetField(ref _detail, value)) OnPropertyChanged(nameof(HasDetail)); } }
    public bool HasDetail => !string.IsNullOrWhiteSpace(_detail);
    public double Progress { get => _progress; private set => SetField(ref _progress, value); }
    public bool IsIndeterminate { get => _isIndeterminate; private set => SetField(ref _isIndeterminate, value); }

    public void Report(string detail)
    {
        Detail = detail;
        var match = Regex.Match(detail, @"(\d{1,3})%");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var percentage) && percentage is >= 0 and <= 100)
        {
            Progress = percentage;
            IsIndeterminate = false;
        }
    }
}

public sealed record BlockerItem(string? Project, string Text, string? ActionLabel = null, RelayCommand? Action = null)
{
    public bool HasProject => !string.IsNullOrWhiteSpace(Project);
    public bool HasAction => Action is not null && !string.IsNullOrWhiteSpace(ActionLabel);
}
