namespace BasisPM.Server.Tests.Infrastructure;

public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir(string? label = null)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "basispm-tests",
            $"{label ?? "srv"}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Combine(params string[] parts)
    {
        var all = new List<string> { Path };
        foreach (var p in parts) all.AddRange(p.Split('/', '\\'));
        return System.IO.Path.Combine(all.ToArray());
    }

    public string WriteFile(string relativePath, string content)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string CreateDir(string relativePath)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch {  }
    }
}
