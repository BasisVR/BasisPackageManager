using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.Views;

/// <summary>
/// Remembers whether each collapsible section was left open or closed. Tag an Expander with
/// <c>views:SectionState.Key="server.runtime"</c>: its XAML <c>IsExpanded</c> is only the default, and once the
/// user opens or closes it, that choice is saved to user settings and applied whenever the tab's view is built
/// again (every tab switch builds a fresh view) and after a restart.
/// </summary>
public sealed class SectionState
{
    public static readonly AttachedProperty<string?> KeyProperty =
        AvaloniaProperty.RegisterAttached<SectionState, Expander, string?>("Key");

    private static readonly Dictionary<string, bool> Remembered = new();
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static UserSettingsService? _settings;

    static SectionState() => KeyProperty.Changed.AddClassHandler<Expander>(OnKeyChanged);

    private SectionState() { }

    public static string? GetKey(Expander expander) => expander.GetValue(KeyProperty);
    public static void SetKey(Expander expander, string? value) => expander.SetValue(KeyProperty, value);

    /// <summary>Starts from the saved states and saves later changes through <paramref name="settings"/> (null keeps them in memory only).</summary>
    public static void Load(UserSettingsService? settings, UserSettings saved)
    {
        _settings = settings;
        Remembered.Clear();
        foreach (var (key, open) in saved.ExpandedSections) Remembered[key] = open;
    }

    /// <summary>Forgets every remembered state, so sections built from now on start at their defaults.</summary>
    public static void Clear() => Remembered.Clear();

    private static event Action<string>? OpenRequested;

    public static void Open(string key)
    {
        Remember(key, true);
        OpenRequested?.Invoke(key);
    }

    private static void OnKeyChanged(Expander expander, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not string key) return;
        void OnOpenRequested(string requested) { if (requested == key) expander.IsExpanded = true; }
        // Restore on attach: XAML has set every attribute by then (its IsExpanded default included) and nothing has drawn
        // yet. Changes only count while attached, so the XAML default is never mistaken for the user's choice.
        expander.AttachedToLogicalTree += (_, _) => { Restore(expander, key); OpenRequested += OnOpenRequested; };
        expander.DetachedFromLogicalTree += (_, _) => OpenRequested -= OnOpenRequested;
        if (((ILogical)expander).IsAttachedToLogicalTree) { Restore(expander, key); OpenRequested += OnOpenRequested; }
        expander.PropertyChanged += (_, args) =>
        {
            if (args.Property == Expander.IsExpandedProperty && ((ILogical)expander).IsAttachedToLogicalTree) Remember(key, expander.IsExpanded);
        };
    }

    private static void Restore(Expander expander, string key)
    {
        if (Remembered.TryGetValue(key, out var open)) expander.IsExpanded = open;
    }

    private static void Remember(string key, bool open)
    {
        if (Remembered.TryGetValue(key, out var was) && was == open) return;
        Remembered[key] = open;
        if (_settings is { } settings) _ = SaveAsync(settings);
    }

    private static async Task SaveAsync(UserSettingsService service)
    {
        await SaveGate.WaitAsync();
        try
        {
            var sections = new Dictionary<string, bool>(Remembered);
            await service.UpdateAsync(settings => settings.ExpandedSections = sections);
        }
        catch (Exception ex) { DiagnosticLog.Write("Saving which sections are open", ex); /* a layout preference isn't worth surfacing a settings-write error */ }
        finally { SaveGate.Release(); }
    }
}
