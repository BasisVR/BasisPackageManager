using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using BasisPM.App.Services;

namespace BasisPM.App.ViewModels;

/// <summary>The Logs tab — a live, newest-first view of this session's activity log.</summary>
public sealed class LogsViewModel : ObservableObject
{
    private readonly LogService _log;

    public ObservableCollection<LogRow> Entries { get; } = new();

    public RelayCommand ClearCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public LogsViewModel(LogService log)
    {
        _log = log;
        ClearCommand = new RelayCommand(() => { _log.Clear(); Entries.Clear(); });
        OpenFolderCommand = new RelayCommand(() => OpenPath(_log.LogDirectory));

        foreach (var e in _log.Snapshot().Reverse()) Entries.Add(new LogRow(e));
        _log.Added += OnAdded;
    }

    public string AllText() => _log.AllText();

    private void OnAdded(LogEntry e) => Dispatcher.UIThread.Post(() =>
    {
        Entries.Insert(0, new LogRow(e));
        if (Entries.Count > 2000) Entries.RemoveAt(Entries.Count - 1);
    });

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch { }
    }
}

public sealed class LogRow
{
    public LogRow(LogEntry e)
    {
        Time = e.Time.ToString("HH:mm:ss");
        Message = e.Message;
        Brush = new SolidColorBrush(Color.Parse(e.Level switch
        {
            LogLevel.Error => "#FF5775",
            LogLevel.Success => "#22C55E",
            _ => "#B7B3D6",
        }));
    }

    public string Time { get; }
    public string Message { get; }
    public IBrush Brush { get; }
}
