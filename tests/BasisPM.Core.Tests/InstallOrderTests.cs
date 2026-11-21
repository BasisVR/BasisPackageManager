using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class InstallOrderTests
{
    private static List<string> Order(string[] queued, Dictionary<string, string[]> deps) =>
        InstallOrder.DependenciesFirst(queued, id => id, id => deps.GetValueOrDefault(id) ?? Array.Empty<string>());

    [Fact]
    public void QueuedDependencyMovesAheadOfItsDependent()
    {
        var order = Order(new[] { "a", "x", "b" }, new() { ["a"] = new[] { "b" } });
        Assert.Equal(new[] { "b", "a", "x" }, order);
    }

    [Fact]
    public void ChainsInstallDeepestFirst()
    {
        var order = Order(new[] { "a", "b", "c" }, new() { ["a"] = new[] { "b" }, ["b"] = new[] { "c" } });
        Assert.Equal(new[] { "c", "b", "a" }, order);
    }

    [Fact]
    public void WalksThroughPackagesThatAreNotQueued()
    {
        var order = Order(new[] { "a", "b" }, new() { ["a"] = new[] { "middle" }, ["middle"] = new[] { "b" } });
        Assert.Equal(new[] { "b", "a" }, order);
    }

    [Fact]
    public void UnrelatedItemsKeepTheirOrder()
    {
        var order = Order(new[] { "c", "a", "b" }, new());
        Assert.Equal(new[] { "c", "a", "b" }, order);
    }

    [Fact]
    public void CyclesKeepEveryItemOnce()
    {
        var order = Order(new[] { "a", "b" }, new() { ["a"] = new[] { "b" }, ["b"] = new[] { "a" } });
        Assert.Equal(2, order.Count);
        Assert.Equal(new[] { "a", "b" }, order.OrderBy(s => s));
    }

    [Fact]
    public void MatchesIdsIgnoringCase()
    {
        var order = Order(new[] { "a", "Com.B" }, new() { ["a"] = new[] { "com.b" } });
        Assert.Equal(new[] { "Com.B", "a" }, order);
    }
}
