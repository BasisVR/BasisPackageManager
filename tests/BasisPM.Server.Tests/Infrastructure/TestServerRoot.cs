namespace BasisPM.Server.Tests.Infrastructure;

public static class TestServerRoot
{
    public static string ServerSourceDir { get; } = LocateServerSource();

    public static string RegistryDataPath => Path.Combine(ServerSourceDir, "App_Data", "registry.json");

    public static string SeedPath(string leaf) => Path.Combine(ServerSourceDir, "seed", leaf);

    public static string FixtureSeedDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "seed");

    private static string LocateServerSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "BasisPM.Server");
            if (File.Exists(Path.Combine(candidate, "BasisPM.Server.csproj")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate src/BasisPM.Server starting from {AppContext.BaseDirectory}");
    }
}
