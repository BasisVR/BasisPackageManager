using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public static class LicenseReportService
{
    public const int MaxLicenseChars = 200_000;
    public const string UnityPackagePrefix = "com.unity.";
    public const string NoLicenseLabel = "No license stated";

    private static readonly string[] LicenseFileNames = { "LICENSE", "LICENCE", "COPYING", "THIRD PARTY NOTICES", "THIRD-PARTY-NOTICES", "THIRD_PARTY_NOTICES", "THIRDPARTYNOTICES" };
    private static readonly string[] CopyleftFamilies = { "AGPL", "LGPL", "GPL", "MPL", "EPL", "CC-BY-SA" };
    private static readonly Regex LicenseToken = new(@"[A-Za-z0-9.+\-]+", RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    private static readonly (string Phrase, string? Also, string License)[] Signatures =
    {
        ("GNU AFFERO GENERAL PUBLIC LICENSE", null, "AGPL"),
        ("GNU LESSER GENERAL PUBLIC LICENSE", null, "LGPL"),
        ("GNU LIBRARY GENERAL PUBLIC LICENSE", null, "LGPL"),
        ("GNU GENERAL PUBLIC LICENSE", null, "GPL"),
        ("MOZILLA PUBLIC LICENSE", "VERSION 2.0", "MPL-2.0"),
        ("MOZILLA PUBLIC LICENSE", null, "MPL"),
        ("ECLIPSE PUBLIC LICENSE", null, "EPL"),
        ("ATTRIBUTION-SHAREALIKE", null, "CC-BY-SA"),
        ("APACHE LICENSE", "VERSION 2.0", "Apache-2.0"),
        ("UNITY COMPANION LICENSE", null, "Unity Companion License"),
        ("THIS IS FREE AND UNENCUMBERED SOFTWARE RELEASED INTO THE PUBLIC DOMAIN", null, "Unlicense"),
        ("CC0 1.0 UNIVERSAL", null, "CC0-1.0"),
        ("BOOST SOFTWARE LICENSE", null, "BSL-1.0"),
        ("PERMISSION IS HEREBY GRANTED, FREE OF CHARGE", null, "MIT"),
        ("REDISTRIBUTION AND USE IN SOURCE AND BINARY FORMS", "NEITHER THE NAME", "BSD-3-Clause"),
        ("REDISTRIBUTION AND USE IN SOURCE AND BINARY FORMS", null, "BSD-2-Clause"),
        ("PERMISSION TO USE, COPY, MODIFY, AND/OR DISTRIBUTE THIS SOFTWARE FOR ANY PURPOSE", null, "ISC"),
        ("THIS SOFTWARE IS PROVIDED 'AS-IS', WITHOUT ANY EXPRESS OR IMPLIED WARRANTY", null, "Zlib"),
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static LicenseReport Build(string unityProjectPath, string project, bool includeUnity = false, CancellationToken ct = default)
    {
        var packages = new List<PackageLicense>();
        foreach (var package in UnityPackageMap.List(unityProjectPath, includeAssets: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!includeUnity && IsUnityPackage(package.Id)) continue;
            packages.Add(Describe(package));
        }
        packages.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return new LicenseReport(project, DateTimeOffset.Now, packages);
    }

    public static Task<LicenseReport> BuildAsync(string unityProjectPath, string project, bool includeUnity = false, CancellationToken ct = default) =>
        Task.Run(() => Build(unityProjectPath, project, includeUnity, ct), ct);

    public static bool IsUnityPackage(string id) => id.StartsWith(UnityPackagePrefix, StringComparison.OrdinalIgnoreCase);

    public static PackageLicense Describe(ProjectPackageRef package)
    {
        var manifest = PackageJsonFile.Read(package.Root);
        var files = ReadLicenseFiles(package.Root);
        var license = string.IsNullOrWhiteSpace(manifest?.License) ? null : manifest!.License!.Trim();
        var detected = DetectedFile(files)?.License;
        var concern = license is null && !files.Any(f => !IsNoticesFile(f.Name)) ? LicenseConcern.Missing
            : IsCopyleft(license) || IsCopyleft(detected) ? LicenseConcern.Copyleft
            : LicenseConcern.None;
        var name = string.IsNullOrWhiteSpace(manifest?.DisplayName) ? package.Id : manifest!.DisplayName.Trim();
        var version = string.IsNullOrWhiteSpace(manifest?.Version) ? null : manifest!.Version.Trim();
        var author = string.IsNullOrWhiteSpace(manifest?.Author?.Name) ? null : manifest!.Author!.Name.Trim();
        return new PackageLicense(package.Id, name, version, license, detected, author, ReadRepository(package.Root), package.Origin, package.Root, files, concern);
    }

    public static bool IsCopyleft(string? license)
    {
        if (string.IsNullOrWhiteSpace(license)) return false;
        foreach (Match token in LicenseToken.Matches(license))
        {
            var value = token.Value;
            if (value.Equals("AND", StringComparison.OrdinalIgnoreCase) || value.Equals("OR", StringComparison.OrdinalIgnoreCase) || value.Equals("WITH", StringComparison.OrdinalIgnoreCase)) continue;
            if (CopyleftFamilies.Any(family => value.Equals(family, StringComparison.OrdinalIgnoreCase) || value.StartsWith(family + "-", StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    public static string? DetectLicense(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var head = Whitespace.Replace(text.Length > 6000 ? text[..6000] : text, " ").ToUpperInvariant().Replace('’', '\'');
        foreach (var (phrase, also, license) in Signatures)
            if (head.Contains(phrase, StringComparison.Ordinal) && (also is null || head.Contains(also, StringComparison.Ordinal))) return license;
        return null;
    }

    public static IReadOnlyList<string> FindLicenseFiles(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(root)
                .Where(path => IsLicenseFileName(Path.GetFileName(path)))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Looking for license files in {root}", ex);
            return Array.Empty<string>();
        }
    }

    public static bool IsLicenseFileName(string name)
    {
        if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = Path.GetFileNameWithoutExtension(name).Trim().ToUpperInvariant();
        return LicenseFileNames.Any(prefix => stem == prefix || stem.StartsWith(prefix + "-", StringComparison.Ordinal) || stem.StartsWith(prefix + "_", StringComparison.Ordinal) || stem.StartsWith(prefix + ".", StringComparison.Ordinal) || stem.StartsWith(prefix + " ", StringComparison.Ordinal));
    }

    public static string GroupOf(PackageLicense package) => package.EffectiveLicense ?? NoLicenseLabel;

    public static bool IsNoticesFile(string name) => Path.GetFileNameWithoutExtension(name).ToUpperInvariant().Replace('-', ' ').Replace('_', ' ').StartsWith("THIRD", StringComparison.Ordinal);

    private static (string File, string License)? DetectedFile(IReadOnlyList<LicenseFileText> files)
    {
        foreach (var file in files)
            if (!IsNoticesFile(file.Name) && DetectLicense(file.Text) is { } license) return (file.Name, license);
        return null;
    }

    public static string ToMarkdown(LicenseReport report)
    {
        var text = new StringBuilder();
        text.Append("# Third-party notices\n\n");
        text.Append($"{report.Project} includes the packages below. This list was made by Basis Package Manager on {report.Created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.\n");
        var groups = report.Packages.GroupBy(GroupOf, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key == NoLicenseLabel ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            text.Append($"\n## {group.Key}\n");
            foreach (var package in group)
            {
                text.Append($"\n### {package.DisplayName}{(package.Version is null ? "" : " " + package.Version)}\n\n");
                text.Append($"- Package: `{package.Id}`\n");
                if (package.License is null && package.DetectedLicense is not null)
                    text.Append($"- License: {package.DetectedLicense}, found in {package.Files[0].Name} (package.json doesn't state one)\n");
                if (package.Author is not null) text.Append($"- Author: {package.Author}\n");
                if (package.Repository is not null) text.Append($"- Source: {package.Repository}\n");
                if (package.Files.Count == 0)
                {
                    text.Append("\nNo license file was found in this package.\n");
                    continue;
                }
                foreach (var file in package.Files)
                {
                    if (package.Files.Count > 1) text.Append($"\n{file.Name}:\n");
                    text.Append("\n````text\n").Append(file.Text.TrimEnd()).Append('\n');
                    if (file.Truncated) text.Append("[...]\n");
                    text.Append("````\n");
                }
            }
        }
        return text.ToString();
    }

    public static string ToJson(LicenseReport report) => JsonSerializer.Serialize(new
    {
        project = report.Project,
        created = report.Created,
        missing = report.Missing.Count,
        copyleft = report.Copyleft.Count,
        packages = report.Packages.Select(p => new
        {
            id = p.Id,
            name = p.DisplayName,
            version = p.Version,
            license = p.License,
            detectedLicense = p.DetectedLicense,
            author = p.Author,
            repository = p.Repository,
            origin = p.Origin.ToString().ToLowerInvariant(),
            licenseFiles = p.Files.Select(f => f.Name),
            concern = p.Concern.ToString().ToLowerInvariant(),
        }),
    }, Json);

    private static IReadOnlyList<LicenseFileText> ReadLicenseFiles(string root)
    {
        var files = new List<LicenseFileText>();
        foreach (var path in FindLicenseFiles(root))
        {
            try
            {
                var text = File.ReadAllText(path);
                var truncated = text.Length > MaxLicenseChars;
                files.Add(new LicenseFileText(Path.GetFileName(path), truncated ? text[..MaxLicenseChars] : text, truncated));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Reading {path}", ex); }
        }
        return files;
    }

    private static string? ReadRepository(string root)
    {
        var path = PackageJsonFile.PathIn(root);
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("repository", out var repository)) return null;
            var url = repository.ValueKind switch
            {
                JsonValueKind.String => repository.GetString(),
                JsonValueKind.Object when repository.TryGetProperty("url", out var inner) && inner.ValueKind == JsonValueKind.String => inner.GetString(),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(url)) return null;
            url = url.Trim();
            return url.StartsWith("git+", StringComparison.OrdinalIgnoreCase) ? url[4..] : url;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }
}
