using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class BasisChannelsTests
{
    [Theory]
    [InlineData("developer", BasisChannel.Developer)]
    [InlineData("Developer", BasisChannel.Developer)]
    [InlineData("long-term-support-20260916", BasisChannel.LongTermSupport)]
    [InlineData("long-term-support", BasisChannel.LongTermSupport)]
    [InlineData("experimental-branch-major-changes", BasisChannel.Experimental)]
    [InlineData("vpm", BasisChannel.Other)]
    [InlineData("main", BasisChannel.Other)]
    [InlineData("", BasisChannel.Other)]
    [InlineData(null, BasisChannel.Other)]
    public void Classifies_basis_branches(string? branch, BasisChannel expected) => Assert.Equal(expected, BasisChannels.Classify(branch));

    [Theory]
    [InlineData("developer", "developer")]
    [InlineData("long-term-support-20260916", "LTS 2026-09-16")]
    [InlineData("long-term-support-2026-06-23", "LTS 2026-06-23")]
    [InlineData("long-term-support", "LTS")]
    [InlineData("experimental-branch-major-changes", "experimental")]
    [InlineData("vpm", "vpm")]
    [InlineData("", "unknown")]
    public void Describes_branches_for_people(string branch, string expected) => Assert.Equal(expected, BasisChannels.Describe(branch));

    [Fact]
    public void Reads_the_date_of_a_long_term_support_branch()
    {
        Assert.Equal(new DateOnly(2026, 9, 16), BasisChannels.LongTermSupportDate("long-term-support-20260916"));
        Assert.Null(BasisChannels.LongTermSupportDate("long-term-support-20261341"));
        Assert.Null(BasisChannels.LongTermSupportDate("developer"));
    }

    [Fact]
    public void Record_lookup_prefers_the_exact_branch_then_the_channel()
    {
        var keys = new[] { "lts", "long-term-support-20260623", "developer", "experimental" };

        Assert.Equal("long-term-support-20260623", BasisChannels.FindRecordKey(keys, "long-term-support-20260623"));
        Assert.Equal("lts", BasisChannels.FindRecordKey(keys, "long-term-support-20260916"));
        Assert.Equal("experimental", BasisChannels.FindRecordKey(keys, "experimental-branch-major-changes"));
        Assert.Equal("developer", BasisChannels.FindRecordKey(keys, "Developer"));
        Assert.Null(BasisChannels.FindRecordKey(keys, "vpm"));
        Assert.Null(BasisChannels.FindRecordKey(keys, null));
    }

    [Fact]
    public void A_different_long_term_support_date_is_not_the_same_record()
    {
        Assert.Null(BasisChannels.FindRecordKey(new[] { "long-term-support-20260623" }, "long-term-support-20260916"));
    }
}
