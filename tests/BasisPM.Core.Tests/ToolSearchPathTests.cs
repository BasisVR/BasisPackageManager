using System.Runtime.InteropServices;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class ToolSearchPathTests
{
    private static readonly string[] NoSystemPaths = Array.Empty<string>();

    private static Func<string, bool> Existing(params string[] folders) => folders.ToHashSet(StringComparer.Ordinal).Contains;

    [Fact]
    public void Windows_is_left_alone()
        => Assert.Null(ToolSearchPath.Augment(@"C:\Windows", OSPlatform.Windows, @"C:\Users\u", null, NoSystemPaths, _ => true));

    [Fact]
    public void A_finder_launch_on_macOS_gets_homebrew_ahead_of_the_apple_tools()
    {
        var path = ToolSearchPath.Augment("/usr/bin:/bin:/usr/sbin:/sbin", OSPlatform.OSX, "/Users/u", null, new[] { "/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin", "/Library/Apple/usr/bin" },
            Existing("/opt/homebrew/bin", "/usr/local/bin", "/Library/Apple/usr/bin", "/usr/local/share/dotnet"));
        Assert.Equal("/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin:/Library/Apple/usr/bin:/usr/local/share/dotnet", path);
    }

    [Fact]
    public void A_terminal_path_that_already_has_everything_is_unchanged()
    {
        var path = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin";
        Assert.Null(ToolSearchPath.Augment(path, OSPlatform.OSX, "/Users/u", null, new[] { "/usr/local/bin", "/usr/bin" }, Existing("/opt/homebrew/bin", "/usr/local/bin")));
    }

    [Fact]
    public void Existing_entries_keep_their_order_even_when_the_user_put_system_folders_first()
    {
        var path = ToolSearchPath.Augment("/usr/bin:/opt/homebrew/bin:/bin", OSPlatform.OSX, "/Users/u", null, NoSystemPaths, Existing("/opt/homebrew/bin", "/usr/local/bin"));
        Assert.Equal("/usr/local/bin:/usr/bin:/opt/homebrew/bin:/bin", path);
    }

    [Fact]
    public void Trailing_slashes_count_as_the_same_folder()
        => Assert.Null(ToolSearchPath.Augment("/opt/homebrew/bin/:/usr/bin", OSPlatform.OSX, "", null, NoSystemPaths, Existing("/opt/homebrew/bin")));

    [Fact]
    public void Linux_appends_user_tool_folders_that_exist()
    {
        var path = ToolSearchPath.Augment("/usr/local/bin:/usr/bin:/bin", OSPlatform.Linux, "/home/u", null, NoSystemPaths,
            Existing("/home/u/.dotnet", "/home/u/.local/bin", "/var/lib/flatpak/exports/bin", "/home/u/.nix-profile/bin", "/usr/games"));
        Assert.Equal("/usr/local/bin:/usr/bin:/bin:/home/u/.dotnet:/home/u/.local/bin:/usr/games:/var/lib/flatpak/exports/bin:/home/u/.nix-profile/bin", path);
    }

    [Fact]
    public void Dotnet_root_comes_before_the_other_additions()
    {
        var path = ToolSearchPath.Augment("/usr/bin", OSPlatform.Linux, "/home/u", "/opt/dotnet", NoSystemPaths, Existing("/opt/dotnet", "/home/u/.dotnet"));
        Assert.Equal("/usr/bin:/opt/dotnet:/home/u/.dotnet", path);
    }

    [Fact]
    public void An_empty_path_still_gets_the_folders()
        => Assert.Equal("/usr/games", ToolSearchPath.Augment(null, OSPlatform.Linux, "/home/u", null, NoSystemPaths, Existing("/usr/games")));

    [Theory]
    [InlineData("relative/bin")]
    [InlineData("/odd:folder")]
    public void Unusable_folders_are_never_added(string dotnetRoot)
        => Assert.Null(ToolSearchPath.Augment("/usr/bin", OSPlatform.Linux, "", dotnetRoot, NoSystemPaths, f => f == dotnetRoot));

    [Fact]
    public void System_paths_are_read_like_path_helper()
    {
        var root = Path.Combine(Path.GetTempPath(), "basispm-paths-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "paths.d"));
            File.WriteAllText(Path.Combine(root, "paths"), "/usr/local/bin\n/usr/bin\n\n# comment\n/bin\n");
            File.WriteAllText(Path.Combine(root, "paths.d", "20-dotnet"), "/usr/local/share/dotnet\n");
            File.WriteAllText(Path.Combine(root, "paths.d", "10-apple"), "/Library/Apple/usr/bin\n");
            Assert.Equal(new[] { "/usr/local/bin", "/usr/bin", "/bin", "/Library/Apple/usr/bin", "/usr/local/share/dotnet" }, ToolSearchPath.ReadSystemPaths(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Missing_system_path_files_read_as_empty()
        => Assert.Empty(ToolSearchPath.ReadSystemPaths(Path.Combine(Path.GetTempPath(), "basispm-missing-" + Guid.NewGuid().ToString("N"))));
}
