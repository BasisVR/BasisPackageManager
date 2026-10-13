using System.Text.Json;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class LicenseReportServiceTests
{
    private const string MitText = "MIT License\n\nCopyright (c) 2026 Someone\n\nPermission is hereby granted, free of charge, to any person obtaining a copy of this software.\n";
    private const string ApacheText = "                                 Apache License\n                           Version 2.0, January 2004\n";
    private const string GplText = "                    GNU GENERAL PUBLIC LICENSE\n                       Version 3, 29 June 2007\n";

    private static string MakeProject(TempDir t)
    {
        var unity = t.CreateDir("Project");
        t.WriteFile("Project/Packages/manifest.json", "{ \"dependencies\": { \"com.test.clone\": \"file:../.basisdev/com.test.clone\", \"com.unity.thing\": \"1.0.0\" } }");
        t.WriteFile("Project/Packages/packages-lock.json", "{ \"dependencies\": { \"com.unity.thing\": { \"version\": \"1.0.0\", \"source\": \"registry\" } } }");
        t.WriteFile("Project/Packages/com.test.mit/package.json", "{ \"name\": \"com.test.mit\", \"displayName\": \"Mit Tool\", \"version\": \"1.2.0\", \"license\": \"MIT\", \"author\": { \"name\": \"Ada\" }, \"repository\": { \"type\": \"git\", \"url\": \"git+https://github.com/ada/mit-tool.git\" } }");
        t.WriteFile("Project/Packages/com.test.mit/LICENSE.md", MitText);
        t.WriteFile("Project/Packages/com.test.mit/LICENSE.md.meta", "fileFormatVersion: 2\n");
        t.WriteFile("Project/Packages/com.test.gpl/package.json", "{ \"name\": \"com.test.gpl\", \"displayName\": \"Gpl Tool\", \"version\": \"0.1.0\", \"license\": \"GPL-3.0-only\", \"author\": \"Grace <grace@example.org>\" }");
        t.WriteFile("Project/Packages/com.test.gpl/COPYING", GplText);
        t.WriteFile("Project/Packages/com.test.none/package.json", "{ \"name\": \"com.test.none\", \"displayName\": \"No License\", \"version\": \"3.0.0\" }");
        t.WriteFile("Project/Packages/com.test.none/Third Party Notices.md", "This package uses some library under the MIT license.\n" + MitText);
        t.WriteFile("Project/.basisdev/com.test.clone/package.json", "{ \"name\": \"com.test.clone\", \"displayName\": \"Clone Tool\", \"version\": \"2.0.0\" }");
        t.WriteFile("Project/.basisdev/com.test.clone/LICENSE", ApacheText);
        t.WriteFile("Project/Library/PackageCache/com.unity.thing@1.0.0/package.json", "{ \"name\": \"com.unity.thing\", \"displayName\": \"Unity Thing\", \"version\": \"1.0.0\" }");
        t.WriteFile("Project/Library/PackageCache/com.unity.thing@1.0.0/LICENSE.md", "Unity Companion License (\"License\")\nSoftware Copyright 2026 Unity Technologies ApS\n");
        return unity;
    }

    [Fact]
    public void Build_reads_every_package_and_flags_missing_and_copyleft_licenses()
    {
        using var t = new TempDir("licenses");
        var unity = MakeProject(t);

        var report = LicenseReportService.Build(unity, "Test Project");

        Assert.Equal(new[] { "Clone Tool", "Gpl Tool", "Mit Tool", "No License" }, report.Packages.Select(p => p.DisplayName));
        var mit = report.Packages.Single(p => p.Id == "com.test.mit");
        Assert.Equal("MIT", mit.License);
        Assert.Equal("1.2.0", mit.Version);
        Assert.Equal("Ada", mit.Author);
        Assert.Equal("https://github.com/ada/mit-tool.git", mit.Repository);
        Assert.Equal(new[] { "LICENSE.md" }, mit.Files.Select(f => f.Name));
        Assert.Equal(LicenseConcern.None, mit.Concern);
        var clone = report.Packages.Single(p => p.Id == "com.test.clone");
        Assert.Null(clone.License);
        Assert.Equal("Apache-2.0", clone.DetectedLicense);
        Assert.Equal(PackageOrigin.Local, clone.Origin);
        Assert.Equal(LicenseConcern.None, clone.Concern);
        var gpl = report.Packages.Single(p => p.Id == "com.test.gpl");
        Assert.Equal("Grace", gpl.Author);
        Assert.Equal(LicenseConcern.Copyleft, gpl.Concern);
        var none = report.Packages.Single(p => p.Id == "com.test.none");
        Assert.Null(none.DetectedLicense);
        Assert.Equal(LicenseConcern.Missing, none.Concern);
        Assert.Equal(new[] { "com.test.none" }, report.Missing.Select(p => p.Id));
        Assert.Equal(new[] { "com.test.gpl" }, report.Copyleft.Select(p => p.Id));
    }

    [Fact]
    public void Unity_packages_are_left_out_unless_asked_for()
    {
        using var t = new TempDir("licenses");
        var unity = MakeProject(t);

        var report = LicenseReportService.Build(unity, "Test Project", includeUnity: true);

        var thing = report.Packages.Single(p => p.Id == "com.unity.thing");
        Assert.Equal(PackageOrigin.Cache, thing.Origin);
        Assert.Equal("Unity Companion License", thing.DetectedLicense);
    }

    [Fact]
    public void Markdown_groups_packages_by_license_with_their_texts()
    {
        using var t = new TempDir("licenses");
        var report = LicenseReportService.Build(MakeProject(t), "Test Project");

        var markdown = LicenseReportService.ToMarkdown(report);

        Assert.StartsWith("# Third-party notices\n\nTest Project includes the packages below.", markdown);
        var apache = markdown.IndexOf("\n## Apache-2.0\n", StringComparison.Ordinal);
        var gpl = markdown.IndexOf("\n## GPL-3.0-only\n", StringComparison.Ordinal);
        var mit = markdown.IndexOf("\n## MIT\n", StringComparison.Ordinal);
        var none = markdown.IndexOf("\n## " + LicenseReportService.NoLicenseLabel + "\n", StringComparison.Ordinal);
        Assert.True(apache > 0 && apache < gpl && gpl < mit && mit < none);
        Assert.Contains("### Mit Tool 1.2.0", markdown);
        Assert.Contains("- Package: `com.test.mit`", markdown);
        Assert.Contains("- Source: https://github.com/ada/mit-tool.git", markdown);
        Assert.Contains("- License: Apache-2.0, found in LICENSE (package.json doesn't state one)", markdown);
        Assert.Contains("Permission is hereby granted, free of charge", markdown);
        Assert.Contains("````text\n", markdown);
        Assert.DoesNotContain("—", markdown);
    }

    [Fact]
    public void Json_summary_lists_packages_and_counts()
    {
        using var t = new TempDir("licenses");
        var report = LicenseReportService.Build(MakeProject(t), "Test Project");

        using var json = JsonDocument.Parse(LicenseReportService.ToJson(report));

        Assert.Equal("Test Project", json.RootElement.GetProperty("project").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("missing").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("copyleft").GetInt32());
        var gpl = json.RootElement.GetProperty("packages").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "com.test.gpl");
        Assert.Equal("copyleft", gpl.GetProperty("concern").GetString());
        Assert.Equal("embedded", gpl.GetProperty("origin").GetString());
        Assert.Equal("COPYING", gpl.GetProperty("licenseFiles")[0].GetString());
    }

    [Theory]
    [InlineData("MIT", false)]
    [InlineData("Apache-2.0 WITH LLVM-exception", false)]
    [InlineData("CC-BY-4.0", false)]
    [InlineData("GPL-3.0-only", true)]
    [InlineData("MIT OR LGPL-2.1-or-later", true)]
    [InlineData("(MIT AND AGPL-3.0)", true)]
    [InlineData("MPL-2.0", true)]
    [InlineData("EPL-2.0", true)]
    [InlineData("CC-BY-SA-4.0", true)]
    [InlineData("GPL", true)]
    [InlineData("LicenseRef-GPLish", false)]
    [InlineData(null, false)]
    public void IsCopyleft_reads_spdx_expressions(string? license, bool copyleft) =>
        Assert.Equal(copyleft, LicenseReportService.IsCopyleft(license));

    [Theory]
    [InlineData("LICENSE", true)]
    [InlineData("LICENSE.md", true)]
    [InlineData("license.txt", true)]
    [InlineData("LICENCE", true)]
    [InlineData("LICENSE-MIT", true)]
    [InlineData("COPYING.LESSER", true)]
    [InlineData("Third Party Notices.md", true)]
    [InlineData("THIRD-PARTY-NOTICES.txt", true)]
    [InlineData("LICENSE.md.meta", false)]
    [InlineData("LicenseInfo.cs", false)]
    [InlineData("README.md", false)]
    public void IsLicenseFileName_matches_the_usual_names(string name, bool matches) =>
        Assert.Equal(matches, LicenseReportService.IsLicenseFileName(name));
}
