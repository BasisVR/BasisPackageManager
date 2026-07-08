using Avalonia.Headless.XUnit;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class PackageRowTests
{
    private static CatalogPackageVersion Entry(
        string name = "com.basis.sdk", string display = "Basis SDK", string version = "1.0.0",
        string? unity = "6000.0", string? author = "BasisVR", string? license = null, string? url = null)
        => new()
        {
            Name = name,
            DisplayName = display,
            Version = version,
            Description = "A package.",
            Unity = unity,
            License = license,
            Url = url,
            Author = author is null ? null : new CatalogAuthor { Name = author },
        };

    [AvaloniaFact]
    public void Not_installed_row_shows_install()
    {
        var row = new PackageRow(Entry(), installedVersion: null);
        Assert.False(row.IsInstalled);
        Assert.Equal("Install", row.ButtonLabel);
    }

    [AvaloniaFact]
    public void Installed_row_shows_update()
    {
        var row = new PackageRow(Entry(), installedVersion: "1.0.0");
        Assert.True(row.IsInstalled);
        Assert.Equal("Update", row.ButtonLabel);
    }

    [AvaloniaFact]
    public void Passes_through_catalog_fields()
    {
        var row = new PackageRow(Entry(display: "Basis SDK", version: "2.1.0"), null);
        Assert.Equal("Basis SDK", row.DisplayName);
        Assert.Equal("com.basis.sdk", row.Name);
        Assert.Equal("2.1.0", row.Version);
        Assert.Equal("A package.", row.Description);
    }

    [AvaloniaFact]
    public void Author_and_unity_presence_flags()
    {
        var withBoth = new PackageRow(Entry(unity: "6000.0", author: "BasisVR"), null);
        Assert.True(withBoth.HasAuthor);
        Assert.Equal("BasisVR", withBoth.Author);
        Assert.True(withBoth.HasUnity);

        var withNeither = new PackageRow(Entry(unity: null, author: null), null);
        Assert.False(withNeither.HasAuthor);
        Assert.Equal("", withNeither.Author);
        Assert.False(withNeither.HasUnity);
    }

    [AvaloniaFact]
    public void License_presence_flag_and_passthrough()
    {
        var withLicense = new PackageRow(Entry(license: "MIT AND Unlicense"), null);
        Assert.True(withLicense.HasLicense);
        Assert.Equal("MIT AND Unlicense", withLicense.License);

        var noLicense = new PackageRow(Entry(license: null), null);
        Assert.False(noLicense.HasLicense);
    }

    [AvaloniaTheory]
    [InlineData("Basis SDK", "B")]
    [InlineData("  spaced name", "S")]
    [InlineData("", "?")]
    public void Initial_is_the_first_letter_or_question_mark(string display, string expected)
    {
        Assert.Equal(expected, new PackageRow(Entry(display: display), null).Initial);
    }

    [AvaloniaTheory]
    [InlineData("com.unity.2d.sprite", "Unity")]
    [InlineData("com.basis.sdk", "Basis")]
    [InlineData("foo", "Foo")]
    [InlineData("", "Other")]
    public void Owner_is_derived_from_the_package_id(string name, string expectedOwner)
    {
        Assert.Equal(expectedOwner, new PackageRow(Entry(name: name), null).Owner);
    }

    // A package mounted for editing lives as a local folder in Packages/, not the registry git URL.
    // A root-level mount even drops its manifest line (so IsInstalled is false) — it must still read as
    // "Locally mounted", never "available to install" (the reported bug).
    [AvaloniaFact]
    public void Root_mounted_row_is_locally_mounted_not_installable()
    {
        var row = new PackageRow(Entry(), installedVersion: null, isUnofficial: false, isMounted: true);
        Assert.True(row.IsMounted);
        Assert.False(row.IsAvailableToInstall);
        Assert.False(row.IsManageable);
        Assert.Equal("Locally mounted", row.MountedLabel);
    }

    // A subfolder mount keeps a "file:" manifest line (IsInstalled is true) but is still mounted, so it
    // shows "Locally mounted" rather than the Manage / Update path.
    [AvaloniaFact]
    public void File_mounted_row_is_mounted_not_manageable()
    {
        var row = new PackageRow(Entry(), installedVersion: "file:../pkg", isUnofficial: false, isMounted: true);
        Assert.True(row.IsInstalled);
        Assert.True(row.IsMounted);
        Assert.False(row.IsManageable);
        Assert.False(row.IsAvailableToInstall);
    }

    // Install clones the package in, so a mounted package is the installed state: it can be updated,
    // re-versioned and removed (no more "swap back" to a git-URL dependency).
    [AvaloniaFact]
    public void Mounted_row_is_manageable_update_version_remove()
    {
        var row = new PackageRow(Entry(url: "https://github.com/x/x.git"), installedVersion: null, isMounted: true);
        Assert.True(row.HasGit);
        Assert.True(row.CanChooseVersion);
        Assert.True(row.CanUpdate);
        Assert.True(row.CanRemove);
        Assert.False(row.IsAvailableToInstall);
    }

    // A not-installed row can only be installed — not updated or removed.
    [AvaloniaFact]
    public void Available_row_cannot_update_or_remove()
    {
        var row = new PackageRow(Entry(url: "https://github.com/x/x.git"), installedVersion: null);
        Assert.True(row.IsAvailableToInstall);
        Assert.False(row.CanUpdate);
        Assert.False(row.CanRemove);
    }

    [AvaloniaFact]
    public void Unmounted_rows_keep_install_and_manage_states()
    {
        var notInstalled = new PackageRow(Entry(), installedVersion: null);
        Assert.True(notInstalled.IsAvailableToInstall);
        Assert.False(notInstalled.IsManageable);
        Assert.False(notInstalled.IsMounted);

        var installed = new PackageRow(Entry(), installedVersion: "https://github.com/x/x.git");
        Assert.True(installed.IsManageable);
        Assert.False(installed.IsAvailableToInstall);
    }

    [AvaloniaFact]
    public void InstalledLabel_extracts_the_pinned_git_ref()
    {
        var row = new PackageRow(Entry(url: "https://github.com/x/x.git"), installedVersion: "https://github.com/x/x.git#v1.2.0");
        Assert.Equal("v1.2.0", row.InstalledLabel);
        Assert.True(row.HasInstalledVersion);
        Assert.Equal("Installed v1.2.0", row.InstalledVersionText);
    }

    [AvaloniaFact]
    public void InstalledLabel_extracts_the_ref_past_a_path_query()
    {
        var row = new PackageRow(Entry(), installedVersion: "https://github.com/x/x.git?path=Packages/com.x#v2.0.0");
        Assert.Equal("v2.0.0", row.InstalledLabel);
    }

    [AvaloniaFact]
    public void InstalledLabel_reads_default_branch_for_a_git_url_without_a_ref()
    {
        var row = new PackageRow(Entry(), installedVersion: "https://github.com/x/x.git");
        Assert.Equal("default branch", row.InstalledLabel);
        Assert.True(row.HasInstalledVersion);
    }

    [AvaloniaFact]
    public void InstalledLabel_passes_through_a_registry_semver_range()
    {
        Assert.Equal("1.2.0", new PackageRow(Entry(), installedVersion: "1.2.0").InstalledLabel);
        Assert.Equal("^1.0.0", new PackageRow(Entry(), installedVersion: "^1.0.0").InstalledLabel);
    }

    [AvaloniaFact]
    public void InstalledLabel_is_absent_when_not_installed()
    {
        var notInstalled = new PackageRow(Entry(), installedVersion: null);
        Assert.Null(notInstalled.InstalledLabel);
        Assert.False(notInstalled.HasInstalledVersion);
    }

    // A mounted package's live manifest points at the local folder, so its version comes from the git
    // URL it was cloned from (the mount's original manifest value) — the version pill still shows.
    [AvaloniaFact]
    public void Mounted_row_shows_version_from_the_clone_source()
    {
        var mounted = new PackageRow(Entry(), installedVersion: null, isMounted: true,
            mountOriginalValue: "https://github.com/x/x.git#v1.2.0");
        Assert.Equal("v1.2.0", mounted.InstalledLabel);
        Assert.True(mounted.HasInstalledVersion);

        // A subfolder mount keeps a "file:" manifest line, but the version still comes from the clone source.
        var fileMounted = new PackageRow(Entry(), installedVersion: "file:../pkg", isMounted: true,
            mountOriginalValue: "https://github.com/x/x.git#v2.0.0");
        Assert.Equal("v2.0.0", fileMounted.InstalledLabel);

        // A mount with no recorded clone source has no version to show.
        var noSource = new PackageRow(Entry(), installedVersion: "file:../pkg", isMounted: true);
        Assert.Null(noSource.InstalledLabel);
        Assert.False(noSource.HasInstalledVersion);
    }

    // A package installed straight from git and not yet mounted can be mounted for editing; a semver
    // (registry) install, a not-installed row, or an already-mounted package cannot.
    [AvaloniaFact]
    public void CanMountToEdit_only_for_installed_git_packages_not_yet_mounted()
    {
        Assert.True(new PackageRow(Entry(), installedVersion: "https://github.com/x/x.git").CanMountToEdit);
        Assert.False(new PackageRow(Entry(), installedVersion: "1.2.0").CanMountToEdit);
        Assert.False(new PackageRow(Entry(), installedVersion: null).CanMountToEdit);
        Assert.False(new PackageRow(Entry(), installedVersion: "https://github.com/x/x.git", isMounted: true).CanMountToEdit);
    }

    // The amber "edited" state and its label are driven by MountedHasEdits, which the background git
    // scan flips on after the row is built.
    [AvaloniaFact]
    public void MountedHasEdits_drives_the_changed_flag_and_label()
    {
        var row = new PackageRow(Entry(), installedVersion: null, isMounted: true);
        Assert.False(row.IsChanged);
        Assert.Equal("Locally mounted", row.MountedStateLabel);

        row.MountedHasEdits = true;
        Assert.True(row.IsChanged);
        Assert.Equal("Local edits", row.MountedStateLabel);
    }

    // Cache drift (accidental Library/PackageCache edits) also turns the row amber, without a mount.
    [AvaloniaFact]
    public void HasDrift_also_marks_the_row_changed()
    {
        var row = new PackageRow(Entry(url: "https://github.com/x/x.git"), installedVersion: "https://github.com/x/x.git#v1") { HasDrift = true };
        Assert.True(row.IsChanged);
    }

    // The mounted working-clone folder is surfaced for the Open-folder action.
    [AvaloniaFact]
    public void MountFolder_is_exposed_when_provided()
    {
        var row = new PackageRow(Entry(), installedVersion: null, isMounted: true, mountFolder: "C:/proj/Packages/com.x");
        Assert.True(row.HasMountFolder);
        Assert.Equal("C:/proj/Packages/com.x", row.MountFolder);
        Assert.False(new PackageRow(Entry(), installedVersion: null).HasMountFolder);
    }
}
