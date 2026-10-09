using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BasisPM.Cli;

internal enum Tone { Plain, Dim, Bold, Accent, Good, Warn, Bad }

internal readonly record struct Span(string Text, Tone Tone = Tone.Plain)
{
    public static implicit operator Span(string text) => new(text);
}

internal static partial class Out
{
    private const int StdOutput = -11, StdError = -12;
    private const uint VirtualTerminalProcessing = 0x0004;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private static readonly List<(nint Handle, uint Mode)> restoreModes = new();
    private static Encoding? restoreEncoding;
    private static bool outputTerminal, errorTerminal;
    private static string? status;
    private static int statusLength;
    private static Timer? spinner;
    private static int frame;
    private static string lastProgressLabel = "";

    public static bool Color { get; private set; }
    public static bool ErrorColor { get; private set; }
    public static bool Unicode { get; private set; }
    public static bool Live { get; private set; }
    public static bool Piped { get; private set; }
    public static bool JsonMode { get; set; }
    public static bool AnsiOutput => outputTerminal;
    public static bool IsBusy { get { lock (Gate) return spinner is not null; } }

    public static string Check => Unicode ? "✓" : "+";
    public static string Cross => Unicode ? "✗" : "x";
    public static string Dot => Unicode ? "•" : "-";
    public static string Ellipsis => Unicode ? "…" : "...";
    public static string Arrow => Unicode ? "→" : "->";
    private static string[] Frames => Unicode ? new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" } : new[] { "|", "/", "-", "\\" };

    public static void Start()
    {
        try
        {
            restoreEncoding = Console.OutputEncoding;
            if (restoreEncoding.CodePage != Encoding.UTF8.CodePage) Console.OutputEncoding = Encoding.UTF8;
            else restoreEncoding = null;
        }
        catch (IOException) { restoreEncoding = null; }
        outputTerminal = !Console.IsOutputRedirected && EnableVirtualTerminal(StdOutput);
        errorTerminal = !Console.IsErrorRedirected && EnableVirtualTerminal(StdError);
    }

    public static void Restore()
    {
        StopStatus();
        try
        {
            if (restoreEncoding is not null) Console.OutputEncoding = restoreEncoding;
            if (OperatingSystem.IsWindows())
                foreach (var (handle, mode) in restoreModes) SetConsoleMode(handle, mode);
        }
        catch (IOException) { }
    }

    public static void Configure(bool noColorFlag)
    {
        Func<string, string?> env = Environment.GetEnvironmentVariable;
        Color = ShouldUseColor(Console.IsOutputRedirected, outputTerminal, noColorFlag, env("FORCE_COLOR"), env("NO_COLOR"), env("TERM"));
        ErrorColor = ShouldUseColor(Console.IsErrorRedirected, errorTerminal, noColorFlag, env("FORCE_COLOR"), env("NO_COLOR"), env("TERM"));
        Unicode = !Console.IsOutputRedirected && SupportsUnicode(OperatingSystem.IsWindows(), env);
        Live = errorTerminal && env("TERM") != "dumb";
        Piped = Console.IsOutputRedirected;
    }

    internal static bool ShouldUseColor(bool redirected, bool terminal, bool noColorFlag, string? forceColor, string? noColor, string? term)
    {
        if (noColorFlag) return false;
        if (!string.IsNullOrEmpty(forceColor) && forceColor is not ("0" or "false")) return true;
        if (!string.IsNullOrEmpty(noColor) || string.Equals(term, "dumb", StringComparison.Ordinal)) return false;
        return !redirected && terminal;
    }

    internal static bool SupportsUnicode(bool windows, Func<string, string?> env)
    {
        if (!windows) return env("TERM") != "linux";
        return !string.IsNullOrEmpty(env("WT_SESSION")) || !string.IsNullOrEmpty(env("TERMINUS_SUBLIME"))
            || env("ConEmuTask") == "{cmd::Cmder}" || env("TERM_PROGRAM") is "vscode" or "Terminus-Sublime"
            || env("TERM") is "xterm-256color" or "alacritty" or "rxvt-unicode" or "rxvt-unicode-256color" || env("TERMINAL_EMULATOR") == "JetBrains-JediTerm";
    }

    public static int Width
    {
        get
        {
            try { if (!Console.IsOutputRedirected) return Math.Clamp(Console.WindowWidth, 40, 200); }
            catch (IOException) { }
            return int.TryParse(Environment.GetEnvironmentVariable("COLUMNS"), out var columns) && columns >= 40 ? columns : 80;
        }
    }

    public static void Line() => Write(Console.Out, Color, Array.Empty<Span>());
    public static void Line(string text) => Write(Console.Out, Color, new Span[] { text });
    public static void Line(params Span[] spans) => Write(Console.Out, Color, spans);

    public static void Say(params Span[] spans)
    {
        if (JsonMode) Write(Console.Error, ErrorColor, spans);
        else Write(Console.Out, Color, spans);
    }

    public static void Say(string text) => Say(new Span[] { text });
    public static void Success(string text) => Say(new Span(Check + " ", Tone.Good), text);
    public static void Note(string text) => Say(new Span(text, Tone.Dim));
    public static void Heading(string text) => Say(new Span(text, Tone.Bold));
    public static void Warning(string text) => Write(Console.Error, ErrorColor, new Span[] { new("! ", Tone.Warn), text });
    public static void Hint(string text) => Write(Console.Error, ErrorColor, new Span[] { new(text, Tone.Dim) });
    public static void Error(string text) => Write(Console.Error, ErrorColor, new Span[] { new("Error: ", Tone.Bad), text });

    public static void Json(object value)
    {
        lock (Gate)
        {
            ClearStatus();
            Console.Out.Write(JsonSerializer.Serialize(value, JsonOptions) + "\n");
            DrawStatus();
        }
    }

    public static string Paint(string text, Tone tone, bool color) => !color || tone == Tone.Plain || text.Length == 0 ? text : Code(tone) + text + "\x1b[0m";

    private static string Code(Tone tone) => tone switch
    {
        Tone.Dim => "\x1b[90m",
        Tone.Bold => "\x1b[1m",
        Tone.Accent => "\x1b[36m",
        Tone.Good => "\x1b[32m",
        Tone.Warn => "\x1b[33m",
        Tone.Bad => "\x1b[31m",
        _ => "",
    };

    private static void Write(TextWriter writer, bool color, IReadOnlyList<Span> spans)
    {
        var text = new StringBuilder();
        foreach (var span in spans) text.Append(Paint(span.Text, span.Tone, color));
        text.Append(Environment.NewLine);
        lock (Gate)
        {
            ClearStatus();
            writer.Write(text.ToString());
            DrawStatus();
        }
    }

    public static void Prompt(string text, Tone tone = Tone.Plain)
    {
        lock (Gate)
        {
            StopStatusLocked();
            Console.Error.Write(Paint(text, tone, ErrorColor));
        }
    }

    public static void Table(IReadOnlyList<Span[]> rows, IReadOnlyList<string>? header = null, int indent = 2)
    {
        if (Piped && !JsonMode)
        {
            foreach (var row in rows) Line(string.Join('\t', row.Select(cell => cell.Text.Trim())));
            return;
        }
        var all = header is null ? rows.ToList() : rows.Prepend(header.Select(h => new Span(h, Tone.Dim)).ToArray()).ToList();
        if (all.Count == 0) return;
        var widths = new int[all.Max(r => r.Length)];
        foreach (var row in all)
            for (var i = 0; i < row.Length - 1; i++) widths[i] = Math.Max(widths[i], row[i].Text.Length);
        foreach (var row in all)
        {
            var spans = new List<Span> { new string(' ', indent) };
            for (var i = 0; i < row.Length; i++)
            {
                spans.Add(row[i]);
                if (i < row.Length - 1) spans.Add(new string(' ', widths[i] - row[i].Text.Length + 2));
            }
            Say(spans.ToArray());
        }
    }

    public static IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            if (paragraph.Length <= width) { yield return paragraph; continue; }
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width) { yield return line.ToString(); line.Clear(); }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) yield return line.ToString();
        }
    }

    public static string Truncate(string text, int width) =>
        width <= 0 ? "" : text.Length <= width ? text : width <= Ellipsis.Length ? text[..width] : text[..(width - Ellipsis.Length)] + Ellipsis;

    public static void Busy(string text)
    {
        lock (Gate)
        {
            if (!Live) return;
            status = text;
            spinner ??= new Timer(_ => Tick(), null, Unicode ? 80 : 130, Unicode ? 80 : 130);
            DrawStatus();
        }
    }

    public static void Progress(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return;
        lock (Gate)
        {
            if (spinner is not null) { status = line; DrawStatus(); return; }
            var label = ProgressLabel().Match(line);
            if (label.Success)
            {
                var same = label.Groups[1].Value == lastProgressLabel;
                lastProgressLabel = label.Groups[1].Value;
                if (same && !line.Contains("done", StringComparison.OrdinalIgnoreCase) && !line.Contains("100%", StringComparison.Ordinal)) return;
            }
            else lastProgressLabel = "";
        }
        Write(Console.Error, ErrorColor, new Span[] { new(line, Tone.Dim) });
    }

    public static void StopStatus()
    {
        lock (Gate) StopStatusLocked();
    }

    private static void StopStatusLocked()
    {
        spinner?.Dispose();
        spinner = null;
        ClearStatus();
        status = null;
        lastProgressLabel = "";
    }

    private static void Tick()
    {
        lock (Gate)
        {
            if (spinner is null) return;
            frame = (frame + 1) % Frames.Length;
            DrawStatus();
        }
    }

    private static void DrawStatus()
    {
        if (status is null || spinner is null) return;
        var text = Truncate(status.Replace('\t', ' '), Width - 4);
        var line = Paint(Frames[frame % Frames.Length], Tone.Accent, ErrorColor) + " " + Paint(text, Tone.Dim, ErrorColor);
        Console.Error.Write("\r" + line + (errorTerminal ? "\x1b[K" : new string(' ', Math.Max(0, statusLength - text.Length - 2))));
        statusLength = text.Length + 2;
    }

    private static void ClearStatus()
    {
        if (statusLength == 0) return;
        Console.Error.Write(errorTerminal ? "\r\x1b[K" : "\r" + new string(' ', Math.Min(statusLength, Width - 1)) + "\r");
        statusLength = 0;
    }

    private static bool EnableVirtualTerminal(int which)
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            var handle = GetStdHandle(which);
            if (handle == 0 || handle == -1 || !GetConsoleMode(handle, out var mode)) return false;
            if ((mode & VirtualTerminalProcessing) != 0) return true;
            if (!SetConsoleMode(handle, mode | VirtualTerminalProcessing)) return false;
            restoreModes.Add((handle, mode));
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint handle, uint mode);

    [GeneratedRegex(@"^([^:]{3,40}):\s+\d{1,3}%")]
    private static partial Regex ProgressLabel();
}
