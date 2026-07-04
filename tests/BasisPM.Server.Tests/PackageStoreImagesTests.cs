using BasisPM.Server.Models;
using BasisPM.Server.Services;
using BasisPM.Server.Tests.Infrastructure;
using Xunit;

namespace BasisPM.Server.Tests;

public sealed class PackageStoreImagesTests
{
    private static RegistryPackage WithImage(string id, string gitUrl, string? image, string? repoUrl = null)
        => new() { Id = id, Name = id, GitUrl = gitUrl, RepoUrl = repoUrl, Image = image };

    [Fact]
    public void Keeps_a_raw_image_hosted_in_the_packages_own_github_repo()
    {
        var pkg = WithImage("com.a", "https://github.com/owner/repo.git",
            "https://raw.githubusercontent.com/owner/repo/main/promo.png");

        PackageStore.ResolveImages(new[] { pkg }, iconsDir: "does-not-exist");

        Assert.Equal("https://raw.githubusercontent.com/owner/repo/main/promo.png", pkg.Image);
    }

    [Fact]
    public void Keeps_a_raw_image_hosted_in_the_packages_own_gitlab_repo()
    {
        var pkg = WithImage("com.a", "https://gitlab.com/group/repo.git",
            "https://gitlab.com/group/repo/-/raw/main/icon.png");

        PackageStore.ResolveImages(new[] { pkg }, iconsDir: "does-not-exist");

        Assert.Equal("https://gitlab.com/group/repo/-/raw/main/icon.png", pkg.Image);
    }

    [Fact]
    public void Drops_an_image_hosted_in_a_different_repo()
    {
        var pkg = WithImage("com.a", "https://github.com/owner/repo.git",
            "https://raw.githubusercontent.com/someone-else/repo/main/x.png");

        PackageStore.ResolveImages(new[] { pkg }, iconsDir: "does-not-exist");

        Assert.Null(pkg.Image);
    }

    [Fact]
    public void Drops_an_arbitrary_remote_image()
    {
        var pkg = WithImage("com.a", "https://github.com/owner/repo.git", "https://evil.example.com/tracker.png");

        PackageStore.ResolveImages(new[] { pkg }, iconsDir: "does-not-exist");

        Assert.Null(pkg.Image);
    }

    [Fact]
    public void Falls_back_to_a_self_hosted_icon_when_present()
    {
        using var t = new TempDir();
        var icons = t.CreateDir("icons");
        File.WriteAllText(Path.Combine(icons, "com.a.png"), "png-bytes");
        var pkg = WithImage("com.a", "https://github.com/owner/repo.git", image: null);

        PackageStore.ResolveImages(new[] { pkg }, icons);

        Assert.Equal("icons/com.a.png", pkg.Image);
    }

    [Fact]
    public void Self_hosted_icon_honours_extension_precedence()
    {
        using var t = new TempDir();
        var icons = t.CreateDir("icons");
        File.WriteAllText(Path.Combine(icons, "com.a.webp"), "bytes");
        var pkg = WithImage("com.a", "https://github.com/owner/repo.git", image: null);

        PackageStore.ResolveImages(new[] { pkg }, icons);

        Assert.Equal("icons/com.a.webp", pkg.Image);
    }

    [Fact]
    public void Unsafe_id_never_resolves_to_an_icon()
    {
        using var t = new TempDir();
        var icons = t.CreateDir("icons");
        var pkg = WithImage("../evil", "https://github.com/owner/repo.git", image: null);

        PackageStore.ResolveImages(new[] { pkg }, icons);

        Assert.Null(pkg.Image);
    }
}
