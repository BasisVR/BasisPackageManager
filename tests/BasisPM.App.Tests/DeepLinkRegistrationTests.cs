using BasisPM.App.Services;
using Xunit;

namespace BasisPM.App.Tests;

/// <summary>
/// The <c>basispm://</c> scheme registration is now cross-platform. The Windows (reg.exe) and Linux
/// (xdg-mime) side effects can't be unit-tested, but the Linux <c>.desktop</c> handler body is a pure
/// function, so its correctness — the part that actually decides whether links route on Linux — is covered.
/// </summary>
public sealed class DeepLinkRegistrationTests
{
    [Fact]
    public void LinuxDesktopEntry_declares_the_scheme_handler_and_exec()
    {
        var entry = DeepLink.LinuxDesktopEntry("/opt/basispm/BasisPM.App");

        Assert.StartsWith("[Desktop Entry]", entry);
        Assert.Contains("Type=Application", entry);
        Assert.Contains("Exec=\"/opt/basispm/BasisPM.App\" %u", entry);   // %u forwards the URL
        Assert.Contains($"MimeType=x-scheme-handler/{DeepLink.Scheme};", entry);
        Assert.Contains("NoDisplay=true", entry);                          // handler-only, not a menu entry
    }

    [Fact]
    public void LinuxDesktopFile_is_the_expected_handler_id()
        => Assert.Equal("basispm-url-handler.desktop", DeepLink.LinuxDesktopFile);

    [Fact]
    public void LinuxDesktopEntry_uses_the_scheme_constant()
        => Assert.Contains("x-scheme-handler/basispm;", DeepLink.LinuxDesktopEntry("/x"));
}
