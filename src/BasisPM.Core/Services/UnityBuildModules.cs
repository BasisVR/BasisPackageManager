using System.Runtime.InteropServices;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public static class UnityBuildModules
{
    public static readonly string[] CoreClr = { "windows-coreclr", "linux-coreclr", "mac-coreclr" };
    private static readonly string[] Offer = CoreClr.Concat(new[] { "android", "windows-il2cpp", "windows-mono", "linux-il2cpp", "linux-mono", "mac-il2cpp", "mac-mono", "documentation" }).ToArray();

    public static string HostPlatform(OSPlatform os) => os == OSPlatform.Windows ? "WINDOWS" : os == OSPlatform.OSX ? "MAC_OS" : "LINUX";

    public static string HostArchitecture(Architecture architecture) => architecture == Architecture.Arm64 ? "ARM64" : "X86_64";

    public static IReadOnlyList<string>? Available(UnityRelease release, OSPlatform os, Architecture architecture)
    {
        var downloads = (release.Downloads ?? new()).Where(d => d.Platform == HostPlatform(os)).ToList();
        var download = downloads.FirstOrDefault(d => d.Architecture == HostArchitecture(architecture)) ?? downloads.FirstOrDefault();
        return download?.Modules?.Select(m => m.Id).ToList();
    }

    public static IReadOnlyList<string> Offered(IEnumerable<string> available)
    {
        var set = available.ToHashSet(StringComparer.Ordinal);
        return Offer.Where(set.Contains).ToList();
    }

    public static IReadOnlyList<string> OfferedFor(UnityRelease release, OSPlatform os, Architecture architecture) =>
        Available(release, os, architecture) is { Count: > 0 } available ? Offered(available) : Fallback(os, release.Version);

    public static IReadOnlyList<string> Fallback(OSPlatform os, string? version)
    {
        var host = Own(os);
        var available = new List<string> { "android", "linux-il2cpp", "documentation" };
        if (HasCoreClr(version)) available.AddRange(CoreClr);
        available.Add(host == "windows" ? "windows-il2cpp" : "windows-mono");
        available.Add(host == "mac" ? "mac-il2cpp" : "mac-mono");
        if (host != "linux") available.Add("linux-mono");
        return Offered(available);
    }

    public static IReadOnlyList<string> Preselected(OSPlatform os, IEnumerable<string> offered)
    {
        var host = Own(os) + "-coreclr";
        return offered.Where(m => m is "windows-coreclr" or "linux-coreclr" || m == host).ToList();
    }

    public static bool HasCoreClr(string? version) => UnityVersion.TryParse(version, out var v) && (v.Major > 6000 || v.Major == 6000 && v.Minor >= 7);

    private static string Own(OSPlatform os) => os == OSPlatform.Windows ? "windows" : os == OSPlatform.OSX ? "mac" : "linux";
}
