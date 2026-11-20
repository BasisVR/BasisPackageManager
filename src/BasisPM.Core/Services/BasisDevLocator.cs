namespace BasisPM.Core.Services;

public static class BasisDevLocator
{
    public const int MaxLevels = 4;

    public static string Resolve(string projectPath) => Find(projectPath) ?? DefaultFor(projectPath);

    public static string? Find(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        var folder = Path.GetFullPath(projectPath);
        for (var level = 0; level <= MaxLevels && folder is not null; level++)
        {
            var candidate = Path.Combine(folder, BasisDevStore.FolderName);
            if (Directory.Exists(candidate)) return candidate;
            folder = Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        return null;
    }

    public static string DefaultFor(string projectPath)
    {
        var project = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Combine(CloneRoot(project) ?? project, BasisDevStore.FolderName);
    }

    public static string? CloneRoot(string projectPath)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parent is null) return null;
        var isClone = Directory.Exists(Path.Combine(parent, ServerPackageService.ServerFolderName))
            && (Directory.Exists(Path.Combine(parent, ".git")) || File.Exists(Path.Combine(parent, ".git")));
        return isClone ? parent : null;
    }
}
