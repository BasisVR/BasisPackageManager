namespace BasisPM.Core.Models;

public enum LicenseConcern { None, Missing, Copyleft }

public sealed record LicenseFileText(string Name, string Text, bool Truncated);

public sealed record PackageLicense(string Id, string DisplayName, string? Version, string? License, string? DetectedLicense, string? Author, string? Repository, PackageOrigin Origin, string Root, IReadOnlyList<LicenseFileText> Files, LicenseConcern Concern)
{
    public bool HasLicenseText => Files.Count > 0;
    public string? EffectiveLicense => License ?? DetectedLicense;
}

public sealed record LicenseReport(string Project, DateTimeOffset Created, IReadOnlyList<PackageLicense> Packages)
{
    public IReadOnlyList<PackageLicense> Missing => Packages.Where(p => p.Concern == LicenseConcern.Missing).ToList();
    public IReadOnlyList<PackageLicense> Copyleft => Packages.Where(p => p.Concern == LicenseConcern.Copyleft).ToList();
}
