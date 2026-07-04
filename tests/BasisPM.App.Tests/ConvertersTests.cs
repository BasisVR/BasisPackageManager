using System.Globalization;
using Avalonia.Data.Converters;
using BasisPM.App.ViewModels;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class ConvertersTests
{
    private static object? Convert(IValueConverter c, object? value)
        => c.Convert(value, typeof(object), null, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, false)]
    public void IntZero(int value, bool expected) => Assert.Equal(expected, Convert(Converters.IntZero, value));

    [Theory]
    [InlineData(3, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void IntNonZero(int value, bool expected) => Assert.Equal(expected, Convert(Converters.IntNonZero, value));

    [Fact]
    public void NotNull_and_IsNull()
    {
        Assert.Equal(true, Convert(Converters.NotNull, "x"));
        Assert.Equal(false, Convert(Converters.NotNull, null));
        Assert.Equal(true, Convert(Converters.IsNull, null));
        Assert.Equal(false, Convert(Converters.IsNull, "x"));
    }

    [Theory]
    [InlineData(true, 0.45)]
    [InlineData(false, 1.0)]
    public void InstalledOpacity(bool installed, double expected)
        => Assert.Equal(expected, Convert(Converters.InstalledOpacity, installed));
}
