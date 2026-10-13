using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.Views;

/// <summary>
/// Remembers whether each collapsible section was left open or closed, and whether each toggle was left on. Tag an
/// Expander or a ToggleButton with <c>views:SectionState.Key="server.runtime"</c>: its XAML <c>IsExpanded</c> or
/// <c>IsChecked</c> is only the default, and once the user changes it, that choice is saved to user settings and
/// applied whenever the tab's view is built again (every tab switch builds a fresh view) and after a restart.
/// </summary>
public sealed class SectionState
{
    public static readonly AttachedProperty<string?> KeyProperty =
        AvaloniaProperty.RegisterAttached<SectionState, Control, string?>("Key");

    private static readonly Dictionary<string, bool> Remembered = new();
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static UserSettingsService? _settings;

    static SectionState()
    {
        KeyProperty.Changed.AddClassHandler<Expander>((expander, e) => OnKeyChanged(expander, e, Expander.IsExpandedProperty));
        KeyProperty.Changed.AddClassHandler<ToggleButton>((toggle, e) => OnKeyChanged(toggle, e, ToggleButton.IsCheckedProperty));
    }

    private SectionState() { }

    public static string? GetKey(Control control) => control.GetValue(KeyProperty);
    public static void SetKey(Control control, string? value) => control.SetValue(KeyProperty, value);

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

    private static void OnKeyChanged(Control control, AvaloniaPropertyChangedEventArgs e, AvaloniaProperty state)
    {
        if (e.NewValue is not string key) return;
        void OnOpenRequested(string requested) { if (requested == key) control.SetValue(state, true); }
        // Restore on attach: XAML has set every attribute by then (its IsExpanded default included) and nothing has drawn
        // yet. Changes only count while attached, so the XAML default is never mistaken for the user's choice.
        control.AttachedToLogicalTree += (_, _) => { Restore(control, key, state); OpenRequested += OnOpenRequested; };
        control.DetachedFromLogicalTree += (_, _) => OpenRequested -= OnOpenRequested;
        if (((ILogical)control).IsAttachedToLogicalTree) { Restore(control, key, state); OpenRequested += OnOpenRequested; }
        control.PropertyChanged += (_, args) =>
        {
            if (args.Property == state && ((ILogical)control).IsAttachedToLogicalTree) Remember(key, control.GetValue(state) is true);
        };
    }

    private static void Restore(Control control, string key, AvaloniaProperty state)
    {
        if (Remembered.TryGetValue(key, out var open)) control.SetValue(state, open);
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
