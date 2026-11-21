namespace BasisPM.Core.Services;

public enum BpmVcs { Git, Plastic, Perforce }

public sealed record BpmIgnoreFile(BpmVcs Vcs, string Path, string Line, bool Covered);

public static class BpmIgnore
{
    private const int MaxLevels = 8;

    public static IReadOnlyList<BpmIgnoreFile> Files(string basisDevRoot)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(basisDevRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
        var name = Path.GetFileName(basisDevRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var files = new List<BpmIgnoreFile>();
        if (FindUp(folder, ".git") is not null) files.Add(Make(BpmVcs.Git, Path.Combine(folder, ".gitignore"), name + "/", folder, name));
        if (FindUp(folder, ".plastic") is { } workspace) files.Add(Make(BpmVcs.Plastic, Path.Combine(workspace, "ignore.conf"), name, folder, name));
        var p4Name = Environment.GetEnvironmentVariable("P4IGNORE") is { Length: > 0 } configured ? Path.GetFileName(configured) : ".p4ignore";
        if (File.Exists(Path.Combine(folder, p4Name)) || Environment.GetEnvironmentVariable("P4IGNORE") is { Length: > 0 } && Environment.GetEnvironmentVariable("P4CLIENT") is { Length: > 0 })
            files.Add(Make(BpmVcs.Perforce, Path.Combine(folder, p4Name), name + "/", folder, name));
        return files;
    }

    public static IReadOnlyList<string> Ensure(string basisDevRoot)
    {
        var written = new List<string>();
        foreach (var file in Files(basisDevRoot).Where(f => !f.Covered))
        {
            var existing = File.Exists(file.Path) ? File.ReadAllText(file.Path) : "";
            var gap = existing.Length == 0 || existing.EndsWith('\n') ? "" : Environment.NewLine;
            File.AppendAllText(file.Path, gap + file.Line + Environment.NewLine);
            written.Add(file.Path);
        }
        return written;
    }

    private static BpmIgnoreFile Make(BpmVcs vcs, string path, string line, string folder, string name)
    {
        var covered = vcs == BpmVcs.Git ? CoveredByGit(folder, name) : Lists(path, name);
        return new BpmIgnoreFile(vcs, path, line, covered);
    }

    private static bool CoveredByGit(string folder, string name)
    {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
        {
            if (Lists(Path.Combine(dir.FullName, ".gitignore"), name)) return true;
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))) break;
        }
        return false;
    }

    private static bool Lists(string path, string name)
    {
        if (!File.Exists(path)) return false;
        try
        {
            return File.ReadLines(path).Select(l => l.Trim()).Any(l => l == name || l == name + "/" || l == "/" + name || l == "/" + name + "/" || l == "**/" + name || l == "**/" + name + "/");
        }
        catch (IOException) { return false; }
    }

    private static string? FindUp(string folder, string marker)
    {
        var dir = new DirectoryInfo(folder);
        for (var level = 0; level <= MaxLevels && dir is not null; level++, dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, marker)) || File.Exists(Path.Combine(dir.FullName, marker))) return dir.FullName;
        return null;
    }
}
