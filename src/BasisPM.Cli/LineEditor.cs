using System.Text;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal sealed record CompletionResult(int Start, IReadOnlyList<string> Candidates)
{
    public static CompletionResult None { get; } = new(0, Array.Empty<string>());
}

internal sealed class LineBuffer
{
    private readonly StringBuilder text = new();

    public int Cursor { get; private set; }
    public int Length => text.Length;
    public string Text => text.ToString();

    public void Set(string value)
    {
        text.Clear().Append(value);
        Cursor = text.Length;
    }

    public void Insert(string value)
    {
        text.Insert(Cursor, value);
        Cursor += value.Length;
    }

    public void Backspace()
    {
        if (Cursor == 0) return;
        text.Remove(--Cursor, 1);
    }

    public void Delete()
    {
        if (Cursor < text.Length) text.Remove(Cursor, 1);
    }

    public void Left() => Cursor = Math.Max(0, Cursor - 1);
    public void Right() => Cursor = Math.Min(text.Length, Cursor + 1);
    public void Home() => Cursor = 0;
    public void End() => Cursor = text.Length;

    public void WordLeft()
    {
        while (Cursor > 0 && text[Cursor - 1] == ' ') Cursor--;
        while (Cursor > 0 && text[Cursor - 1] != ' ') Cursor--;
    }

    public void WordRight()
    {
        while (Cursor < text.Length && text[Cursor] == ' ') Cursor++;
        while (Cursor < text.Length && text[Cursor] != ' ') Cursor++;
    }

    public void DeleteWordBack()
    {
        var end = Cursor;
        WordLeft();
        text.Remove(Cursor, end - Cursor);
    }

    public void KillToEnd() => text.Remove(Cursor, text.Length - Cursor);

    public void KillToStart()
    {
        text.Remove(0, Cursor);
        Cursor = 0;
    }

    public void Replace(int start, string value)
    {
        start = Math.Clamp(start, 0, Cursor);
        text.Remove(start, Cursor - start).Insert(start, value);
        Cursor = start + value.Length;
    }

    public bool ApplyCompletion(CompletionResult result)
    {
        if (result.Candidates.Count == 0) return false;
        var current = Text[Math.Clamp(result.Start, 0, Cursor)..Cursor].Trim('"', '\'');
        if (result.Candidates.Count == 1)
        {
            var only = result.Candidates[0];
            var folder = only.EndsWith(Path.DirectorySeparatorChar) || only.EndsWith('/');
            Replace(result.Start, !folder ? Quote(only) + " " : Quote(only) == only ? only : Quote(only.TrimEnd(Path.DirectorySeparatorChar, '/')));
            return true;
        }
        var common = CommonPrefix(result.Candidates);
        if (common.Length <= current.Length) return false;
        Replace(result.Start, common.Contains(' ') ? "\"" + common : common);
        return true;
    }

    internal static string Quote(string value) => value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'') ? value : "\"" + value.Replace("\"", "\\\"") + "\"";

    internal static string CommonPrefix(IReadOnlyList<string> values)
    {
        var prefix = values[0];
        foreach (var value in values.Skip(1))
        {
            var length = 0;
            while (length < prefix.Length && length < value.Length && char.ToLowerInvariant(prefix[length]) == char.ToLowerInvariant(value[length])) length++;
            prefix = prefix[..length];
        }
        return prefix;
    }
}

internal sealed class LineEditor
{
    private const int MaxHistory = 500;
    private readonly string? historyPath;
    private readonly Func<string, CompletionResult> complete;
    private readonly List<string> history = new();
    private bool loaded;

    public LineEditor(string? historyPath, Func<string, CompletionResult> complete)
    {
        this.historyPath = historyPath;
        this.complete = complete;
    }

    internal IReadOnlyList<string> History => history;

    public string? ReadLine(string prompt, Tone tone)
    {
        LoadHistory();
        if (Console.IsInputRedirected || Console.IsOutputRedirected || Environment.GetEnvironmentVariable("TERM") == "dumb")
        {
            if (!Console.IsInputRedirected) Console.Out.Write(Out.Paint(prompt, tone, Out.Color));
            var plain = Console.ReadLine();
            if (plain is not null) Remember(plain);
            return plain;
        }
        try { return Edit(prompt, tone); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            DiagnosticLog.Write("Reading a line in the interactive console", ex);
            Console.WriteLine();
            Console.Out.Write(Out.Paint(prompt, tone, Out.Color));
            var fallback = Console.ReadLine();
            if (fallback is not null) Remember(fallback);
            return fallback;
        }
    }

    private string? Edit(string prompt, Tone tone)
    {
        var buffer = new LineBuffer();
        var view = new LineView(prompt, tone);
        var index = history.Count;
        var draft = "";
        var tabs = 0;
        var interrupted = false;
        var treatControlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            view.Render(buffer);
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                var control = (key.Modifiers & ConsoleModifiers.Control) != 0 && (key.Modifiers & ConsoleModifiers.Alt) == 0;
                var wasTab = key.Key == ConsoleKey.Tab && key.Modifiers == 0;
                tabs = wasTab ? tabs + 1 : 0;
                if (!(control && key.Key == ConsoleKey.C)) interrupted = false;
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        view.Finish(buffer);
                        Remember(buffer.Text);
                        return buffer.Text;
                    case ConsoleKey.Tab when wasTab:
                        var result = complete(buffer.Text[..buffer.Cursor]);
                        if (buffer.ApplyCompletion(result)) tabs = 0;
                        else if (result.Candidates.Count > 1 && tabs >= 2)
                        {
                            view.Finish(buffer);
                            ShowCandidates(result.Candidates);
                        }
                        break;
                    case ConsoleKey.Backspace when control: buffer.DeleteWordBack(); break;
                    case ConsoleKey.Backspace: buffer.Backspace(); break;
                    case ConsoleKey.Delete: buffer.Delete(); break;
                    case ConsoleKey.LeftArrow when control: buffer.WordLeft(); break;
                    case ConsoleKey.LeftArrow: buffer.Left(); break;
                    case ConsoleKey.RightArrow when control: buffer.WordRight(); break;
                    case ConsoleKey.RightArrow: buffer.Right(); break;
                    case ConsoleKey.Home: buffer.Home(); break;
                    case ConsoleKey.End: buffer.End(); break;
                    case ConsoleKey.Escape: buffer.Set(""); break;
                    case ConsoleKey.UpArrow: Walk(buffer, ref index, ref draft, -1); break;
                    case ConsoleKey.DownArrow: Walk(buffer, ref index, ref draft, 1); break;
                    case ConsoleKey.C when control:
                        if (buffer.Length == 0 && interrupted)
                        {
                            view.Finish(buffer);
                            return null;
                        }
                        view.Finish(buffer, "^C");
                        if (buffer.Length == 0) Out.Hint("Press Ctrl+C again, Ctrl+D or type 'exit' to leave.");
                        interrupted = buffer.Length == 0;
                        buffer.Set("");
                        index = history.Count;
                        break;
                    case ConsoleKey.D when control:
                        if (buffer.Length == 0)
                        {
                            view.Finish(buffer);
                            return null;
                        }
                        buffer.Delete();
                        break;
                    case ConsoleKey.L when control: Console.Clear(); break;
                    case ConsoleKey.A when control: buffer.Home(); break;
                    case ConsoleKey.E when control: buffer.End(); break;
                    case ConsoleKey.B when control: buffer.Left(); break;
                    case ConsoleKey.F when control: buffer.Right(); break;
                    case ConsoleKey.U when control: buffer.KillToStart(); break;
                    case ConsoleKey.K when control: buffer.KillToEnd(); break;
                    case ConsoleKey.W when control: buffer.DeleteWordBack(); break;
                    case ConsoleKey.P when control: Walk(buffer, ref index, ref draft, -1); break;
                    case ConsoleKey.N when control: Walk(buffer, ref index, ref draft, 1); break;
                    default:
                        if (control || key.KeyChar < ' ') continue;
                        buffer.Insert(key.KeyChar.ToString());
                        break;
                }
                view.Render(buffer);
            }
        }
        finally { Console.TreatControlCAsInput = treatControlC; }
    }

    private void Walk(LineBuffer buffer, ref int index, ref string draft, int step)
    {
        if (history.Count == 0) return;
        if (index == history.Count) draft = buffer.Text;
        index = Math.Clamp(index + step, 0, history.Count);
        buffer.Set(index == history.Count ? draft : history[index]);
    }

    private static void ShowCandidates(IReadOnlyList<string> candidates)
    {
        var width = Math.Max(20, Out.Width - 2);
        var cell = Math.Min(width, candidates.Max(c => c.Length) + 2);
        var columns = Math.Max(1, width / cell);
        var shown = candidates.Take(200).ToList();
        for (var row = 0; row < shown.Count; row += columns)
            Out.Line(new Span("  " + string.Concat(shown.Skip(row).Take(columns).Select(c => c.PadRight(cell))).TrimEnd(), Tone.Dim));
        if (candidates.Count > shown.Count) Out.Note($"  {Out.Ellipsis} and {candidates.Count - shown.Count} more");
    }

    internal void Remember(string line)
    {
        var entry = line.Trim();
        if (entry.Length == 0 || history.Count > 0 && history[^1] == entry) return;
        history.Add(entry);
        if (history.Count > MaxHistory) history.RemoveRange(0, history.Count - MaxHistory);
        if (historyPath is null || IsSensitive(entry)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
            File.AppendAllText(historyPath, entry.Replace('\n', ' ') + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write("Saving the console history", ex); }
    }

    internal static bool IsSensitive(string line) =>
        line.Contains("password", StringComparison.OrdinalIgnoreCase) || line.Contains("token", StringComparison.OrdinalIgnoreCase) || line.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(word => word.TrimStart('-').EndsWith("key", StringComparison.OrdinalIgnoreCase));

    private void LoadHistory()
    {
        if (loaded) return;
        loaded = true;
        if (historyPath is null || !File.Exists(historyPath)) return;
        try
        {
            var lines = File.ReadAllLines(historyPath).Where(l => l.Trim().Length > 0).ToList();
            history.AddRange(lines.Skip(Math.Max(0, lines.Count - MaxHistory)));
            if (lines.Count > MaxHistory * 2) File.WriteAllLines(historyPath, history);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write("Loading the console history", ex); }
    }

    private sealed class LineView(string prompt, Tone tone)
    {
        private readonly string painted = Out.Paint(prompt, tone, Out.Color);
        private int scroll, shown;

        public void Render(LineBuffer buffer)
        {
            var room = Math.Max(8, Out.Width - prompt.Length - 1);
            if (buffer.Cursor < scroll) scroll = buffer.Cursor;
            if (buffer.Cursor - scroll > room) scroll = buffer.Cursor - room;
            if (buffer.Length - scroll < room) scroll = Math.Max(0, buffer.Length - room);
            var visible = buffer.Text.Substring(scroll, Math.Min(room, buffer.Length - scroll));
            var clear = Out.AnsiOutput ? "\x1b[K" : new string(' ', Math.Max(0, shown - visible.Length));
            Console.Out.Write("\r" + painted + visible + clear + "\r" + painted + visible[..(buffer.Cursor - scroll)]);
            shown = visible.Length;
        }

        public void Finish(LineBuffer buffer, string suffix = "")
        {
            scroll = 0;
            var text = buffer.Text;
            var clear = Out.AnsiOutput ? "\x1b[K" : new string(' ', Math.Max(0, shown - text.Length));
            Console.Out.Write("\r" + painted + text + clear + Out.Paint(suffix, Tone.Dim, Out.Color) + Environment.NewLine);
            shown = 0;
        }
    }
}
