using System.Net;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class RegistryChecksServiceTests
{
    private const string Sample = """
        {
          "format": 1,
          "generated": "2026-10-14T03:00:00Z",
          "basis": {
            "developer": { "branch": "developer", "commit": "0d4695eb4e262fa59449f57669d6a07580b82171", "unity": "6000.7.0b2" },
            "experimental-branch-major-changes": { "branch": "experimental-branch-major-changes", "commit": "51fa698ed5af6575534b9ff2e8eaf4a2e047ad72" },
            "lts": { "branch": "long-term-support-20260916", "commit": "33fc043c1149b372fa76ca9748d16cc49ba42cb1" }
          },
          "packages": {
            "com.basis.transport.websocket": {
              "experimental-branch-major-changes": { "verdict": "works", "version": "1.0.0", "basisCommit": "51fa698ed5af6575534b9ff2e8eaf4a2e047ad72", "compiled": true },
              "developer": { "verdict": "breaks", "errors": 12, "summary": "Runtime/Transport.cs(3,7): error CS0246: The type or namespace name 'IBasisNetworkPackage' could not be found" }
            },
            "jp.lilxyzw.liltoon": { "lts": { "verdict": "works" } }
          }
        }
        """;

    [Theory]
    [InlineData(null, "https://basisvr.org/packages/checks.json")]
    [InlineData("", "https://basisvr.org/packages/checks.json")]
    [InlineData("https://example.com/registry/catalog.json", "https://example.com/registry/checks.json")]
    [InlineData("https://example.com/registry/", "https://example.com/registry/checks.json")]
    [InlineData("file:///C:/catalog.json", null)]
    [InlineData("not a url", null)]
    public void Checks_live_next_to_the_catalog(string? catalog, string? expected) => Assert.Equal(expected, RegistryChecksService.UrlFor(catalog));

    [Fact]
    public void Finds_the_record_for_a_branch_or_its_channel()
    {
        var file = RegistryChecksService.Parse(Sample);

        Assert.Equal(PackageVerdict.Works, RegistryChecksService.Find(file, "com.basis.transport.websocket", "experimental-branch-major-changes")!.Verdict);
        Assert.Equal(PackageVerdict.Breaks, RegistryChecksService.Find(file, "COM.BASIS.TRANSPORT.WEBSOCKET", "developer")!.Verdict);
        Assert.Equal(12, RegistryChecksService.Find(file, "com.basis.transport.websocket", "developer")!.Errors);
        Assert.Equal(PackageVerdict.Works, RegistryChecksService.Find(file, "jp.lilxyzw.liltoon", "long-term-support-20260916")!.Verdict);
        Assert.Null(RegistryChecksService.Find(file, "jp.lilxyzw.liltoon", "developer"));
        Assert.Null(RegistryChecksService.Find(file, "com.unknown", "developer"));
        Assert.Equal("33fc043c1149b372fa76ca9748d16cc49ba42cb1", RegistryChecksService.Head(file, "long-term-support-20260916")!.Commit);
    }

    [Fact]
    public void Garbage_reads_as_nothing() => Assert.Null(RegistryChecksService.Parse("{ nope"));

    [Fact]
    public async Task Loads_from_the_registry_and_tolerates_a_missing_file()
    {
        var ok = new RegistryChecksService(StubHttpMessageHandler.Always(Sample));
        var missing = new RegistryChecksService(StubHttpMessageHandler.AlwaysStatus(HttpStatusCode.NotFound));
        var offline = new RegistryChecksService(StubHttpMessageHandler.AlwaysThrows());

        Assert.NotNull(await ok.TryLoadAsync("https://example.com/catalog.json"));
        Assert.Null(await missing.TryLoadAsync("https://example.com/catalog.json"));
        Assert.Null(await offline.TryLoadAsync("https://example.com/catalog.json"));
    }

    [Fact]
    public void Set_then_serialize_round_trips_in_a_stable_order()
    {
        var file = new RegistryChecksFile();
        RegistryChecksService.Set(file, "com.z", "developer", new PackageCheckState { Verdict = PackageVerdict.Works }, new BasisCheckTarget { Branch = "developer", Commit = "abc" });
        RegistryChecksService.Set(file, "com.a", "experimental-branch-major-changes", new PackageCheckState { Verdict = PackageVerdict.Breaks, Errors = 2 });

        var json = RegistryChecksService.Serialize(file);
        var back = RegistryChecksService.Parse(json)!;

        Assert.True(json.IndexOf("com.a", StringComparison.Ordinal) < json.IndexOf("com.z", StringComparison.Ordinal));
        Assert.DoesNotContain("\r\n", json);
        Assert.Contains("\"breaks\"", json);
        Assert.Equal(2, back.Packages["com.a"]["experimental-branch-major-changes"].Errors);
        Assert.Equal("abc", back.Basis["developer"].Commit);
        Assert.NotNull(back.Generated);
    }
}
