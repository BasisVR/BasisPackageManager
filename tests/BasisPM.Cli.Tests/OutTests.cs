using Xunit;

namespace BasisPM.Cli.Tests;

public sealed class OutTests
{
    [Theory]
    [InlineData(false, true, false, null, null, null, true)]
    [InlineData(true, true, false, null, null, null, false)]
    [InlineData(false, false, false, null, null, null, false)]
    [InlineData(false, true, true, null, null, null, false)]
    [InlineData(false, true, false, null, "1", null, false)]
    [InlineData(false, true, false, null, "", null, true)]
    [InlineData(false, true, false, null, null, "dumb", false)]
    [InlineData(true, false, false, "1", "1", null, true)]
    [InlineData(true, false, false, "0", null, null, false)]
    [InlineData(false, true, true, "1", null, null, false)]
    public void ColorFollowsFlagsEnvironmentAndTerminal(bool redirected, bool terminal, bool noColorFlag, string? forceColor, string? noColor, string? term, bool expected) =>
        Assert.Equal(expected, Out.ShouldUseColor(redirected, terminal, noColorFlag, forceColor, noColor, term));

    [Fact]
    public void UnicodeSymbolsOnlyWhereTheyRender()
    {
        Assert.True(Out.SupportsUnicode(false, _ => null));
        Assert.False(Out.SupportsUnicode(false, name => name == "TERM" ? "linux" : null));
        Assert.False(Out.SupportsUnicode(true, _ => null));
        Assert.True(Out.SupportsUnicode(true, name => name == "WT_SESSION" ? "abc" : null));
        Assert.True(Out.SupportsUnicode(true, name => name == "TERM_PROGRAM" ? "vscode" : null));
    }

    [Fact]
    public void WrapsOnWordBoundaries()
    {
        var lines = Out.Wrap("one two three four five six", 9).ToList();
        Assert.Equal(new[] { "one two", "three", "four five", "six" }, lines);
        Assert.Equal(new[] { "a", "b" }, Out.Wrap("a\nb", 40));
    }

    [Fact]
    public void TruncatesWithAnEllipsis()
    {
        Assert.Equal("short", Out.Truncate("short", 10));
        Assert.Equal(10, Out.Truncate("a much longer piece of text", 10).Length);
        Assert.EndsWith(Out.Ellipsis, Out.Truncate("a much longer piece of text", 10));
    }

    [Fact]
    public void PaintOnlyAddsCodesWhenColorIsOn()
    {
        Assert.Equal("text", Out.Paint("text", Tone.Good, false));
        Assert.Equal("\u001b[32mtext\u001b[0m", Out.Paint("text", Tone.Good, true));
        Assert.Equal("text", Out.Paint("text", Tone.Plain, true));
    }
}
