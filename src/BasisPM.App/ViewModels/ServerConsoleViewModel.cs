using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using BasisPM.App.Localization;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed class ServerConsoleViewModel : ObservableObject
{
    public const int MaxLines = 5000;
    private const int HistoryLimit = 100;

    private readonly ConcurrentQueue<ServerConsoleLine> _pending = new();
    private readonly List<string> _history = new();
    private int _historyCursor;
    private string _historyDraft = "";
    private int _flushQueued;
    private string _input = "";
    private string _status = "";
    private bool _isRunning;
    private bool _acceptsInput = true;
    private bool _externalWindow;

    public ServerConsoleViewModel()
    {
        SendCommand = new RelayCommand(Send);
        ClearCommand = new RelayCommand(Clear);
    }

    public ObservableCollection<ServerConsoleLineRow> Lines { get; } = new();
    public RelayCommand SendCommand { get; }
    public RelayCommand ClearCommand { get; }
    public event Action<string>? CommandEntered;

    public string Input { get => _input; set => SetField(ref _input, value); }
    public string Status { get => _status; set => SetField(ref _status, value); }
    public bool HasLines => Lines.Count > 0;
    public bool IsVisible => _isRunning || HasLines;
    public bool CanType => _isRunning && _acceptsInput && !_externalWindow;
    public string InputHint => _externalWindow ? L.Tr("server.console.inputOwnWindow") : _acceptsInput ? L.Tr("server.console.inputWatermark") : L.Tr("server.console.inputOff");

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (!SetField(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(IsVisible));
            OnPropertyChanged(nameof(CanType));
        }
    }

    public bool AcceptsInput
    {
        get => _acceptsInput;
        set
        {
            if (!SetField(ref _acceptsInput, value)) return;
            OnPropertyChanged(nameof(CanType));
            OnPropertyChanged(nameof(InputHint));
        }
    }

    public bool ExternalWindow
    {
        get => _externalWindow;
        set
        {
            if (!SetField(ref _externalWindow, value)) return;
            OnPropertyChanged(nameof(CanType));
            OnPropertyChanged(nameof(InputHint));
        }
    }

    public void Append(ServerConsoleLine line)
    {
        _pending.Enqueue(line);
        if (Interlocked.Exchange(ref _flushQueued, 1) == 0) Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
    }

    public void Notice(string text, ServerConsoleLevel level = ServerConsoleLevel.Notice) => Append(new ServerConsoleLine(DateTimeOffset.Now, level, text));

    public void Flush()
    {
        Interlocked.Exchange(ref _flushQueued, 0);
        var hadLines = HasLines;
        while (_pending.TryDequeue(out var line)) Lines.Add(new ServerConsoleLineRow(line));
        var overflow = Lines.Count - MaxLines;
        for (var i = 0; i < overflow; i++) Lines.RemoveAt(0);
        if (hadLines == HasLines) return;
        OnPropertyChanged(nameof(HasLines));
        OnPropertyChanged(nameof(IsVisible));
    }

    public void Clear()
    {
        _pending.Clear();
        Lines.Clear();
        OnPropertyChanged(nameof(HasLines));
        OnPropertyChanged(nameof(IsVisible));
    }

    public string AllText() => string.Join(Environment.NewLine, Lines.Select(r => r.Text));

    public void Recall(int direction)
    {
        if (_history.Count == 0) return;
        if (_historyCursor == _history.Count) _historyDraft = Input;
        var next = Math.Clamp(_historyCursor + direction, 0, _history.Count);
        if (next == _historyCursor) return;
        _historyCursor = next;
        Input = next == _history.Count ? _historyDraft : _history[next];
    }

    private void Send()
    {
        var text = Input.Trim();
        if (text.Length == 0 || !CanType) return;
        if (_history.Count == 0 || !string.Equals(_history[^1], text, StringComparison.Ordinal)) _history.Add(text);
        if (_history.Count > HistoryLimit) _history.RemoveAt(0);
        _historyCursor = _history.Count;
        _historyDraft = "";
        Input = "";
        CommandEntered?.Invoke(text);
    }
}

public sealed class ServerConsoleLineRow
{
    private static readonly IBrush InfoBrush = new ImmutableSolidColorBrush(Color.Parse("#E4E1F7"));
    private static readonly IBrush WarningBrush = new ImmutableSolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush ErrorBrush = new ImmutableSolidColorBrush(Color.Parse("#FF5775"));
    private static readonly IBrush InputBrush = new ImmutableSolidColorBrush(Color.Parse("#C084FC"));
    private static readonly IBrush NoticeBrush = new ImmutableSolidColorBrush(Color.Parse("#8F8BB3"));

    public ServerConsoleLineRow(ServerConsoleLine line)
    {
        Level = line.Level;
        Time = line.Time.ToString("HH:mm:ss");
        Text = line.Level == ServerConsoleLevel.Input ? "> " + line.Text : line.Text;
        Brush = line.Level switch
        {
            ServerConsoleLevel.Warning => WarningBrush,
            ServerConsoleLevel.Error => ErrorBrush,
            ServerConsoleLevel.Input => InputBrush,
            ServerConsoleLevel.Notice => NoticeBrush,
            _ => InfoBrush,
        };
    }

    public ServerConsoleLevel Level { get; }
    public string Time { get; }
    public string Text { get; }
    public IBrush Brush { get; }
    public bool IsNotice => Level == ServerConsoleLevel.Notice;
}
