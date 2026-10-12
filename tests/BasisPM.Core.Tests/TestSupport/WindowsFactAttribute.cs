using Xunit;

namespace BasisPM.Core.Tests.TestSupport;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows file locking";
    }
}
