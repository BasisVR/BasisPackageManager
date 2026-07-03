namespace BasisPM.Core.Models;

public sealed record DetectionResult(bool IsValid, string? ResolvedPath, string Reason)
{
    public static DetectionResult Ok(string path, string reason = "") => new(true, path, reason);
    public static DetectionResult Fail(string reason) => new(false, null, reason);
}
