using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using BasisPM.App.Views;
using BasisPM.Core.Models;

namespace BasisPM.App.Services;

/// <summary>Modal prompts shown over the main window.</summary>
public static class Dialogs
{
    private static Window? Owner =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d ? d.MainWindow : null;

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
}
