using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class PackageScanServiceTests
{
    private static PackageScan ScanSource(string source, string path = "Editor/Tool.cs")
    {
        using var t = new TempDir("scan");
        t.WriteFile("package.json", """{ "name": "com.example.tool", "version": "1.0.0" }""");
        t.WriteFile(path, source);
        return PackageScanService.Scan(t.Path);
    }

    private static IEnumerable<PackageCapability> Capabilities(PackageScan scan) => scan.Findings.Select(f => f.Capability);

    [Fact]
    public void An_ordinary_package_has_no_findings()
    {
        var scan = ScanSource("""
            using UnityEngine;
            namespace Example
            {
                public sealed class Spinner : MonoBehaviour
                {
                    public float speed = 90f;
                    private void Update() => transform.Rotate(0f, speed * Time.deltaTime, 0f);
                }
            }
            """, "Runtime/Spinner.cs");

        Assert.Empty(scan.Findings);
        Assert.Equal(1, scan.Scripts);
        Assert.False(scan.HasHighRisk);
        Assert.False(scan.Incomplete);
    }

    [Fact]
    public void Auto_run_editor_code_that_starts_programs_is_reported_with_its_lines()
    {
        var scan = ScanSource("""
            using UnityEditor;
            using System.Diagnostics;

            [InitializeOnLoad]
            static class Boot
            {
                static Boot()
                {
                    Process.Start("calc.exe");
                }
            }
            """);

        var auto = Assert.Single(scan.Findings, f => f.Capability == PackageCapability.RunsAutomatically);
        Assert.Equal(4, auto.Line);
        Assert.Equal(PackageRisk.Caution, auto.Risk);
        var start = Assert.Single(scan.Findings, f => f.Evidence == "Process.Start");
        Assert.Equal(9, start.Line);
        Assert.Equal(PackageRisk.High, start.Risk);
        Assert.Equal("Editor/Tool.cs:9", start.Location);
        Assert.Equal(PackageCapability.StartsPrograms, scan.Capabilities[0]);
        Assert.True(scan.HasHighRisk);
    }

    [Fact]
    public void Comments_and_ordinary_strings_are_not_read_as_code()
    {
        var scan = ScanSource(""""
            class Notes
            {
                // Process.Start("old way");
                /* Assembly.Load(bytes);
                   new WebClient(); */
                string hint = "call Process.Start( yourself";
                string verbatim = @"He said ""Assembly.Load("" here";
                char quote = '"';
                string raw = """ [DllImport("x")] """;
            }
            """");

        Assert.Empty(scan.Findings);
    }

    [Fact]
    public void Code_hidden_in_interpolation_holes_escapes_and_line_breaks_is_still_found()
    {
        var scan = ScanSource("""
            class Sneaky
            {
                string a = $"{System.Diagnostics.Process.Start("calc")}";
                void B() { \u0050rocess.Start("calc"); }
                void C() { System.Reflection.Assembly
                    .Load(new byte[0]); }
            }
            """);

        Assert.Contains(scan.Findings, f => f.Capability == PackageCapability.StartsPrograms && f.Line == 3);
        Assert.Contains(scan.Findings, f => f.Evidence == "Process.Start" && f.Line == 4);
        Assert.Contains(scan.Findings, f => f.Evidence == "Assembly.Load" && f.Line == 5);
    }

    [Fact]
    public void A_preprocessor_line_cannot_open_a_string_that_hides_code()
    {
        var scan = ScanSource("""
            #region @"
            class Hidden { void Run() { System.Diagnostics.Process.Start("calc"); } }
            #endregion "
            """);

        Assert.Contains(scan.Findings, f => f.Capability == PackageCapability.StartsPrograms && f.Line == 2);
    }

    [Fact]
    public void String_literals_are_checked_for_logins_webhooks_commands_and_blobs()
    {
        var blob = new string('A', 3000);
        var scan = ScanSource($$"""
            class Grab
            {
                string tokens = System.IO.Path.Combine(root, "discord\\Local Storage\\leveldb");
                string hook = "https://discord.com/api/webhooks/1/abc";
                string shell = "cmd.exe /c whoami";
                string payload = "{{blob}}";
            }
            """);

        Assert.Contains(scan.Findings, f => f.Capability == PackageCapability.ReadsLogins && f.Line == 3);
        Assert.Contains(scan.Findings, f => f.Capability == PackageCapability.Network && f.Risk == PackageRisk.High && f.Line == 4);
        Assert.Contains(scan.Findings, f => f.Capability == PackageCapability.StartsPrograms && f.Line == 5);
        Assert.Contains(scan.Findings, f => f.Capability == PackageCapability.EncodedData && f.Line == 6);
    }

    [Fact]
    public void Ordinary_uses_of_risky_words_are_not_flagged()
    {
        var scan = ScanSource("""
            class Fine
            {
                System.Type t = System.Reflection.Assembly.Load("Assembly-CSharp-Editor").GetType("Some.Type");
                string label = "ignores a separate local state";
                [System.Runtime.InteropServices.DllImport("__Internal")] static extern void Send(string message);
            }
            """);

        Assert.DoesNotContain(scan.Findings, f => f.Capability is PackageCapability.LoadsHiddenCode or PackageCapability.ReadsLogins);
        var import = Assert.Single(scan.Findings);
        Assert.Equal(PackageCapability.NativeCode, import.Capability);
        Assert.Equal(PackageRisk.Caution, import.Risk);
        Assert.Equal("[DllImport(\"__Internal\")]", import.Evidence);
    }

    [Fact]
    public void Loading_code_from_bytes_or_files_and_reading_browser_files_are_flagged()
    {
        var scan = ScanSource("""
            class Bad
            {
                void A(byte[] data) => System.Reflection.Assembly.Load(data);
                void B() => System.Reflection.Assembly.LoadFrom("C:/Temp/x.dll");
                string state = System.IO.Path.Combine(chrome, "Local State");
                [System.Runtime.InteropServices.DllImport("kernel32")] static extern int GetTickCount();
            }
            """);

        Assert.Contains(scan.Findings, f => f.Capability == PackageCapability.LoadsHiddenCode && f.Line == 3);
        Assert.Contains(scan.Findings, f => f.Capability == PackageCapability.LoadsHiddenCode && f.Line == 4);
        Assert.Contains(scan.Findings, f => f.Capability == PackageCapability.ReadsLogins && f.Line == 5);
        Assert.Contains(scan.Findings, f => f.Evidence == "[DllImport(\"kernel32\")]" && f.Risk == PackageRisk.High);
    }

    [Fact]
    public void Embedded_program_bytes_count_as_hidden_code()
    {
        var scan = ScanSource("class Blob { static readonly byte[] Data = { 0x4D, 0x5A, 0x90, 0x00, 0x03 }; }");

        Assert.Contains(scan.Findings, f => f.Capability == PackageCapability.LoadsHiddenCode && f.Evidence == "embedded program bytes");
    }

    [Fact]
    public void Binaries_are_classified_by_what_they_are_not_what_they_are_called()
    {
        using var t = new TempDir("scan");
        t.WriteFile("package.json", """{ "name": "com.example.bin" }""");
        t.CreateDir("Textures");
        t.CreateDir("Plugins");
        File.WriteAllBytes(t.Combine("Textures/icon.png"), new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00 });
        File.WriteAllBytes(t.Combine("Textures/real.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A });
        File.WriteAllBytes(t.Combine("Plugins/libnative.so"), new byte[] { 0x7F, 0x45, 0x4C, 0x46 });
        File.WriteAllBytes(t.Combine("Plugins/odd.dll"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        t.WriteFile("Tools/setup.bat", "@echo off");
        File.Copy(typeof(GitService).Assembly.Location, t.Combine("Plugins/Managed.dll"));

        var scan = PackageScanService.Scan(t.Path);

        Assert.Contains(scan.Findings, f => f.File == "Textures/icon.png" && f.Capability == PackageCapability.Programs);
        Assert.DoesNotContain(scan.Findings, f => f.File == "Textures/real.png");
        Assert.Contains(scan.Findings, f => f.File == "Plugins/libnative.so" && f.Capability == PackageCapability.NativeCode);
        Assert.Contains(scan.Findings, f => f.File == "Plugins/odd.dll" && f.Capability == PackageCapability.NativeCode);
        Assert.Contains(scan.Findings, f => f.File == "Tools/setup.bat" && f.Capability == PackageCapability.Programs);
        Assert.Contains(scan.Findings, f => f.File == "Plugins/Managed.dll" && f.Capability == PackageCapability.CompiledCode);
        Assert.Contains(scan.Findings, f => f.File == "Plugins/Managed.dll" && f.Capability == PackageCapability.StartsPrograms && f.Evidence == "System.Diagnostics.Process");
        Assert.Equal(5, scan.Binaries);
    }

    [Fact]
    public void Compiler_plugins_are_reported_from_meta_labels_and_response_files()
    {
        using var t = new TempDir("scan");
        t.WriteFile("package.json", """{ "name": "com.example.gen" }""");
        t.CreateDir("Analyzers");
        File.WriteAllBytes(t.Combine("Analyzers/Gen.dll"), new byte[] { 1, 2, 3, 4 });
        t.WriteFile("Analyzers/Gen.dll.meta", "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\nlabels:\n- RoslynAnalyzer\n");
        t.WriteFile("Runtime/csc.rsp", "-nowarn:0169\n-a:\"../Analyzers/Other.dll\"\n-r:C:/Tools/Extra.dll\n");

        var scan = PackageScanService.Scan(t.Path);

        Assert.Contains(scan.Findings, f => f.File == "Analyzers/Gen.dll" && f.Capability == PackageCapability.RunsInCompiler);
        Assert.Contains(scan.Findings, f => f.File == "Runtime/csc.rsp" && f.Capability == PackageCapability.RunsInCompiler && f.Line == 2);
        Assert.Contains(scan.Findings, f => f.File == "Runtime/csc.rsp" && f.Capability == PackageCapability.CompiledCode && f.Line == 3);
        Assert.Equal(PackageCapability.RunsInCompiler, scan.Capabilities[0]);
    }

    [Fact]
    public void Hidden_folders_are_skipped_but_tilde_folders_are_scanned()
    {
        using var t = new TempDir("scan");
        t.WriteFile("package.json", """{ "name": "com.example.server", "basisServer": { "nuget": { "Some.Library": "1.2.3" } } }""");
        t.WriteFile(".git/hooks/post-checkout.cs", "class H { void R() { System.Diagnostics.Process.Start(\"x\"); } }");
        t.WriteFile(".github/workflows/Build.cs", "class W { void R() { System.Diagnostics.Process.Start(\"x\"); } }");
        t.WriteFile("Server~/BasisNetworkServer/Plugin.cs", "class P { void R() { new System.Net.Sockets.TcpClient(); } }");

        var scan = PackageScanService.Scan(t.Path);

        Assert.DoesNotContain(scan.Findings, f => f.File.StartsWith(".", StringComparison.Ordinal));
        Assert.Contains(scan.Findings, f => f.File == "Server~/BasisNetworkServer/Plugin.cs" && f.Capability == PackageCapability.Network);
        Assert.Contains(scan.Findings, f => f.File == "package.json" && f.Evidence.Contains("Some.Library"));
        Assert.Equal(1, scan.Scripts);
    }

    [Fact]
    public void A_missing_folder_scans_as_empty()
    {
        Assert.Same(PackageScan.Empty, PackageScanService.Scan(Path.Combine(Path.GetTempPath(), "basispm-tests", "missing-" + Guid.NewGuid().ToString("N"))));
    }

    [Theory]
    [InlineData("abcdef1234567", "abcdef1234567890abcdef1234567890abcdef12", true)]
    [InlineData("ABCDEF1234567890ABCDEF1234567890ABCDEF12", "abcdef1234567890abcdef1234567890abcdef12", true)]
    [InlineData("abcdef1", "abcdef2234567890abcdef1234567890abcdef12", false)]
    [InlineData("abc", "abcdef1234567890abcdef1234567890abcdef12", false)]
    [InlineData(null, "abcdef1234567890abcdef1234567890abcdef12", false)]
    public void Commits_match_on_a_shared_prefix_of_at_least_seven_characters(string? a, string? b, bool same)
        => Assert.Equal(same, PackageScanService.SameCommit(a, b));
}
