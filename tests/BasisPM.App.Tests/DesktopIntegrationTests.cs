using BasisPM.App.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class DesktopIntegrationTests
{
    [Theory]
    [InlineData("/home/u/Apps/BasisPackageManager.AppImage", "\"/home/u/Apps/BasisPackageManager.AppImage\"")]
    [InlineData("/home/u/My Apps/Basis.AppImage", "\"/home/u/My Apps/Basis.AppImage\"")]
    [InlineData("/home/u/$cash/50%/a\"b`c\\d", "\"/home/u/\\\\$cash/50%%/a\\\\\"b\\\\`c\\\\\\\\d\"")]
    public void Exec_paths_survive_desktop_entry_escaping(string path, string expected) => Assert.Equal(expected, DesktopIntegration.ExecQuote(path));

    [Fact]
    public void The_menu_entry_launches_the_appimage_and_handles_basispm_links()
    {
        var entry = DesktopIntegration.MenuEntry("/home/u/Basis.AppImage", "/home/u/.local/share/BasisPM/basispm.png");
        Assert.StartsWith("[Desktop Entry]\n", entry);
        Assert.Contains("\nExec=\"/home/u/Basis.AppImage\" %u\n", entry);
        Assert.Contains("\nIcon=/home/u/.local/share/BasisPM/basispm.png\n", entry);
        Assert.Contains($"\nMimeType=x-scheme-handler/{DeepLink.Scheme};\n", entry);
        Assert.DoesNotContain("NoDisplay", entry);
    }

    [Fact]
    public void The_menu_entry_leaves_out_a_missing_icon() => Assert.DoesNotContain("Icon=", DesktopIntegration.MenuEntry("/x", null));
}
