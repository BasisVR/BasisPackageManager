using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests.TestSupport;

public static class GitProbe
{
    public static readonly bool Available = new GitService().IsAvailable;
}

public sealed class GitFactAttribute : FactAttribute
{
    public GitFactAttribute()
    {
        if (!GitProbe.Available) Skip = "Requires git on PATH";
    }
}
