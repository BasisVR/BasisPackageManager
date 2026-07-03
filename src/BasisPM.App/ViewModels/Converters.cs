using System.Globalization;
using Avalonia.Data.Converters;

namespace BasisPM.App.ViewModels;

public static class Converters
{
    public static readonly IValueConverter IntZero = new FuncValueConverter<int, bool>(v => v == 0);
    public static readonly IValueConverter IntNonZero = new FuncValueConverter<int, bool>(v => v > 0);
    public static readonly IValueConverter NotNull = new FuncValueConverter<object?, bool>(v => v is not null);
    public static readonly IValueConverter IsNull = new FuncValueConverter<object?, bool>(v => v is null);
    public static readonly IValueConverter InstalledOpacity = new FuncValueConverter<bool, double>(v => v ? 0.45 : 1.0);
}
