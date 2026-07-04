using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace BasisPM.App.Localization;

/// <summary>
/// XAML markup extension: <c>{loc:Tr some.key}</c> resolves a property to the active language's value
/// for <c>some.key</c> and updates it live when the language changes.
/// </summary>
/// <remarks>
/// It binds to <see cref="Localizer.CurrentCode"/> (a normal INotifyPropertyChanged property that changes
/// on <see cref="Localizer.SetLanguage"/>) and runs the value through a converter that returns the
/// translation for <c>Key</c>. Binding to a plain property is reliably observed by Avalonia — unlike a
/// custom string indexer, which Avalonia does not watch for change notifications.
/// </remarks>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    /// <summary>The localization key (positional in XAML: <c>{loc:Tr some.key}</c>).</summary>
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding(nameof(Localizer.CurrentCode))
    {
        Source = Localizer.Instance,
        Converter = TrConverter.Instance,
        ConverterParameter = Key,
        Mode = BindingMode.OneWay,
    };

    private sealed class TrConverter : IValueConverter
    {
        public static readonly TrConverter Instance = new();

        // Ignores the bound value (the language code) and returns the translation for the key parameter.
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Localizer.Instance.Get(parameter as string ?? "");

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
