using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using BasisPM.App.Localization;
using BasisPM.App.Views;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.Services;

/// <summary>Modal prompts shown over the main window.</summary>
public static class Dialogs
{
    private static Window? Owner =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d ? d.MainWindow : null;

    private static FilePickerFileType PackageListFileType => new("Basis package list")
    {
        Patterns = new[] { "*.json" },
        MimeTypes = new[] { "application/json" },
    };

    /// <summary>Asks for a display name; returns the trimmed value, or null if cancelled/empty.</summary>
    public static async Task<string?> PromptAliasAsync(string title, string path, string suggested)
    {
        var owner = Owner;
        if (owner is null) return null;
        return await new AliasPromptWindow(title, path, suggested).ShowDialog<string?>(owner);
    }

    /// <summary>Asks which install to target; returns the chosen one, or null if cancelled.</summary>
    public static async Task<BasisInstall?> PickInstallAsync(string title, IReadOnlyList<BasisInstall> installs)
    {
        var owner = Owner;
        if (owner is null) return null;
        return await new InstallPickerWindow(title, installs).ShowDialog<BasisInstall?>(owner);
    }

    /// <summary>A yes/no prompt; returns true only if the user chose Yes.</summary>
    public static async Task<bool> ConfirmAsync(string title, string message)
    {
        var owner = Owner;
        if (owner is null) return false;
        return await new ConfirmWindow(title, message).ShowDialog<bool>(owner);
    }

    /// <summary>Shows the create-package-list dialog; returns the draft, or null if cancelled.</summary>
    public static async Task<PackageListDraft?> CreatePackageListAsync(string suggestedName, string basisLine, IReadOnlyList<PackageListEntry> candidates)
    {
        var owner = Owner;
        if (owner is null) return null;
        return await new CreatePackageListWindow(suggestedName, basisLine, candidates).ShowDialog<PackageListDraft?>(owner);
    }

    /// <summary>Prompts for a save location and writes the package-list JSON there; returns the saved path, or null if cancelled.</summary>
    public static async Task<string?> SavePackageListFileAsync(string suggestedFileName, string json)
    {
        var owner = Owner;
        if (owner is null) return null;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.Tr("packages.picker.savePackageList"),
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            FileTypeChoices = new[] { PackageListFileType },
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return null;
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    /// <summary>Prompts the user to pick a local package-list file; returns its path, or null if cancelled.</summary>
    public static async Task<string?> OpenPackageListFileAsync()
    {
        var owner = Owner;
        if (owner is null) return null;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.Tr("packages.picker.openPackageList"),
            AllowMultiple = false,
            FileTypeFilter = new[] { PackageListFileType },
        });
        return files?.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>Collects pull-request details for a mounted package; returns the request, or null if cancelled.</summary>
    public static async Task<PrRequest?> SubmitPrAsync(string packageId)
    {
        var owner = Owner;
        if (owner is null) return null;
        var id = string.IsNullOrWhiteSpace(packageId) ? "package" : packageId;
        var slug = new string(id.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        return await new SubmitPrWindow(id, $"basis-edit-{slug}").ShowDialog<PrRequest?>(owner);
    }

    /// <summary>Prompts for a GitHub personal access token (used on demand when a PR needs one); returns it, or null if cancelled.</summary>
    public static async Task<string?> SignInAsync()
    {
        var owner = Owner;
        if (owner is null) return null;
        return await new SignInPromptWindow().ShowDialog<string?>(owner);
    }

    /// <summary>Shows the version picker; returns the chosen version, or null if cancelled.</summary>
    public static async Task<PackageVersionOption?> PickVersionAsync(string title, PackageVersions versions)
    {
        var owner = Owner;
        if (owner is null) return null;
        return await new VersionPickerWindow(title, versions).ShowDialog<PackageVersionOption?>(owner);
    }

    /// <summary>Shows the branch picker; returns the chosen branch, or null if cancelled.</summary>
    public static async Task<string?> PickBranchAsync(string title, IReadOnlyList<string> branches, string current)
    {
        var owner = Owner;
        if (owner is null) return null;
        return await new BranchPickerWindow(title, branches, current).ShowDialog<string?>(owner);
    }
}
