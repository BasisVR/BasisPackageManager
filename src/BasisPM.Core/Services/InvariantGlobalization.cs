using System.Runtime.InteropServices;

namespace BasisPM.Core.Services;

public static class InvariantGlobalization
{
    public static void EnableIfIcuMissing()
    {
        if (IsNeeded(Platform.Current, Environment.GetEnvironmentVariable("CLR_ICU_VERSION_OVERRIDE"), CanLoad)) AppContext.SetSwitch("System.Globalization.Invariant", true);
    }

    public static bool IsNeeded(OSPlatform os, string? icuVersionOverride, Func<string, bool> canLoad) =>
        os == OSPlatform.Linux && icuVersionOverride is null && !Enumerable.Range(50, 206).Any(major => canLoad("libicuuc.so." + major) && canLoad("libicui18n.so." + major));

    public static bool CanLoad(string library)
    {
        if (!NativeLibrary.TryLoad(library, out var handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }
}
