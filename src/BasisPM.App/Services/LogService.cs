using System.Text;

namespace BasisPM.App.Services;

public enum LogLevel { Info, Success, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message);

/// <summary>
/// An in-memory + on-disk record of what the app did this session — drives the Logs tab and gives
/// support something to attach. Written to <c>%AppData%/BasisPM/logs/session-*.log</c>.
/// </summary>
public sealed class LogService
{
    private const int MaxInMemory = 2000;
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = new();
    private readonly string? _file;

    public string LogDirectory { get; }

    public event Action<LogEntry>? Added;

    public LogService()
    {
        LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BasisPM", "logs");
        try
        {
            Directory.CreateDirectory(LogDirectory);
            _file = Path.Combine(LogDirectory, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        }
        catch { _file = null; }
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate) return _entries.ToList();
    }

    public void Add(LogLevel level, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var entry = new LogEntry(DateTime.Now, level, Redact.Scrub(message));
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxInMemory) _entries.RemoveAt(0);
        }
        try { if (_file is not null) File.AppendAllText(_file, $"{entry.Time:yyyy-MM-dd HH:mm:ss} [{level}] {message}\n"); }
        catch { }
        Added?.Invoke(entry);
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    public string AllText()
    {
        var sb = new StringBuilder();
        foreach (var e in Snapshot()) sb.AppendLine($"{e.Time:HH:mm:ss} [{e.Level}] {e.Message}");
        return sb.ToString();
    }
}
