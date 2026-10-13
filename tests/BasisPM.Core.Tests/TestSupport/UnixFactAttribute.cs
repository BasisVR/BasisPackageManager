using Xunit;

namespace BasisPM.Core.Tests.TestSupport;

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Requires macOS or Linux symbolic links";
    }
}
