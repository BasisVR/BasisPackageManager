namespace BasisPM.Server.Tests.Infrastructure;

public static class TestServerRoot
{
    public static string ServerSourceDir { get; } = LocateServerSource();

    public static string RegistryDataPath => Path.Combine(ServerSourceDir, "App_Data", "registry.json");

    public static string SeedPath(string leaf) => Path.Combine(ServerSourceDir, "seed", leaf);

    private static string LocateServerSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "BasisPM.Server");
            if (File.Exists(Path.Combine(candidate, "seed", "packages.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate src/BasisPM.Server starting from {AppContext.BaseDirectory}");
    }
}
