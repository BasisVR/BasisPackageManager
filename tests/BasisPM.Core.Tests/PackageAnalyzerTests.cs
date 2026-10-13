using System.Text.Json;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests
{
    public sealed class PackageAnalyzerTests
    {
        private sealed class AnalyzerProject : IDisposable
        {
            private readonly TempDir temp = new("analyzer");

            public AnalyzerProject(string unityVersion = "6000.7.0b4")
            {
                temp.WriteFile("Project/ProjectSettings/ProjectVersion.txt", $"m_EditorVersion: {unityVersion}\n");
                temp.CreateDir("Project/Assets");
                Manifest();
            }

            public string Root => temp.Combine("Project");

            public string Outside(string relative) => temp.Combine(relative);

            public void Manifest(params (string Id, string Value)[] dependencies) =>
                temp.WriteFile("Project/Packages/manifest.json", JsonSerializer.Serialize(new { dependencies = dependencies.ToDictionary(d => d.Id, d => d.Value) }));

            public void RawManifest(string json) => temp.WriteFile("Project/Packages/manifest.json", json);

            public string Package(string id, string? json = null, string? folder = null, bool readme = true)
            {
                var relative = "Packages/" + (folder ?? id);
                temp.WriteFile($"Project/{relative}/package.json", json ?? JsonSerializer.Serialize(new { name = id, version = "1.0.0", displayName = id, description = "Test package.", license = "MIT" }));
                if (readme) temp.WriteFile($"Project/{relative}/README.md", "# " + id);
                return relative;
            }

            public void File(string relative, string content) => temp.WriteFile("Project/" + relative, content);

            public void Asmdef(string folder, string name, string extra = "") =>
                temp.WriteFile($"Project/{folder}/{name}.asmdef", "{ \"name\": \"" + name + "\"" + (extra.Length > 0 ? ", " + extra : "") + " }");

            public void Copy(string source, string relative)
            {
                var target = temp.Combine("Project/" + relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                System.IO.File.Copy(source, target, true);
            }

            public void Metas(string folder)
            {
                var root = temp.Combine("Project/" + folder);
                foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).ToList())
                {
                    if (entry.EndsWith(".meta", StringComparison.Ordinal) || System.IO.File.Exists(entry + ".meta")) continue;
                    System.IO.File.WriteAllText(entry + ".meta", $"fileFormatVersion: 2\nguid: {Guid.NewGuid():N}\n");
                }
            }

            public void Meta(string relative, string guid) => temp.WriteFile($"Project/{relative}.meta", $"fileFormatVersion: 2\nguid: {guid}\n");

            public ProjectInventory Scan(string? editor = null) => new PackageAnalyzer().ScanProject(Root, editor);

            public IReadOnlyList<PackageIssue> Analyze(string? focus = null, string? editor = null, IReadOnlyCollection<string>? installable = null) =>
                new PackageAnalyzer().Analyze(Scan(editor), new PackageAnalyzerOptions { Focus = focus is null ? null : new[] { focus }, Installable = installable });

            public void Dispose() => temp.Dispose();
        }

        private static IEnumerable<PackageIssue> With(IReadOnlyList<PackageIssue> issues, string code) => issues.Where(i => i.Code == code);

        private static PackageIssue Single(IReadOnlyList<PackageIssue> issues, string code) => Assert.Single(issues, i => i.Code == code);

        private static void None(IReadOnlyList<PackageIssue> issues, string code) => Assert.DoesNotContain(issues, i => i.Code == code);

        private static AnalyzerProject Simple(string id, string assembly, string code, string? asmdefExtra = null)
        {
            var project = new AnalyzerProject();
            var folder = project.Package(id);
            project.Asmdef(folder + "/Runtime", assembly, asmdefExtra ?? "");
            project.File(folder + "/Runtime/Code.cs", code);
            return project;
        }

        [Fact]
        public void Global_type_named_like_Unitys_Debug_is_an_error()
        {
            using var project = Simple("com.test.bad", "Com.Test.Bad", "public class Debug { }");
            var issue = Single(project.Analyze(), IssueCodes.GlobalTypeShadows);
            Assert.Equal(IssueSeverity.Error, issue.Severity);
            Assert.Equal("com.test.bad", issue.Package);
            Assert.Contains("Debug.Log", issue.Message);
        }

        [Fact]
        public void Namespaced_or_internal_Debug_is_fine()
        {
            using var project = Simple("com.test.ok", "Com.Test.Ok", "namespace Com.Test.Ok { public class Debug { } }\ninternal class Random { }");
            None(project.Analyze(), IssueCodes.GlobalTypeShadows);
        }

        [Fact]
        public void Global_type_in_an_assembly_nobody_sees_is_not_reported()
        {
            using var project = Simple("com.test.hidden", "Com.Test.Hidden", "public class Debug { }", "\"autoReferenced\": false");
            None(project.Analyze(), IssueCodes.GlobalTypeShadows);
        }

        [Fact]
        public void Same_public_type_in_two_auto_referenced_assemblies_is_a_duplicate()
        {
            using var project = Simple("com.test.bad", "Com.Test.Bad", "namespace Shared { public class Thing { } }");
            var dup = project.Package("com.test.dup");
            project.Asmdef(dup + "/Runtime", "Com.Test.Dup");
            project.File(dup + "/Runtime/Thing.cs", "namespace Shared { public class Thing { } }");
            var issue = Single(project.Analyze(), IssueCodes.TypeDuplicate);
            Assert.Equal(IssueSeverity.Warning, issue.Severity);
            Assert.Contains("CS0433", issue.Message);
            Assert.Contains("Shared.Thing", issue.Message);
        }

        [Fact]
        public void Same_type_without_a_common_consumer_or_internal_is_not_a_duplicate()
        {
            using var project = Simple("com.test.a", "Com.Test.A", "namespace Shared { public class Thing { } internal class Other { } }", "\"autoReferenced\": false");
            var b = project.Package("com.test.b");
            project.Asmdef(b + "/Runtime", "Com.Test.B");
            project.File(b + "/Runtime/Thing.cs", "namespace Shared { public class Thing { } internal class Other { } }");
            None(project.Analyze(), IssueCodes.TypeDuplicate);
        }

        [Fact]
        public void Duplicate_assembly_names_are_an_error()
        {
            using var project = Simple("com.test.bad", "Com.Test.Bad", "namespace A { class X { } }");
            var dup = project.Package("com.test.dup");
            project.Asmdef(dup + "/Runtime2", "Com.Test.Bad");
            project.File(dup + "/Runtime2/X.cs", "namespace B { class X { } }");
            var issues = With(project.Analyze(), IssueCodes.AssemblyDuplicate).ToList();
            Assert.NotEmpty(issues);
            Assert.All(issues, i => Assert.Equal(IssueSeverity.Error, i.Severity));
            Assert.Contains(issues, i => i.Package == "com.test.dup");
        }

        [Fact]
        public void Asmdef_named_like_a_predefined_assembly_is_an_error()
        {
            using var project = Simple("com.test.bad", "Assembly-CSharp", "namespace A { class X { } }");
            Assert.Equal(IssueSeverity.Error, Single(project.Analyze(), IssueCodes.AssemblyDuplicate).Severity);
        }

        [Fact]
        public void Unique_assembly_names_are_fine()
        {
            using var project = Simple("com.test.one", "Com.Test.One", "namespace A { class X { } }");
            None(project.Analyze(), IssueCodes.AssemblyDuplicate);
        }

        [Fact]
        public void Loose_package_scripts_are_not_compiled()
        {
            using var project = Simple("com.test.bad", "Com.Test.Bad", "namespace A { class X { } }");
            project.File("Packages/com.test.bad/NoAsmdef/Loose.cs", "public class LooseInPackage { }");
            project.File("Packages/com.test.bad/Editor/UsesEditor.cs", "public class EditorLoose { }");
            var issues = With(project.Analyze(), IssueCodes.ScriptsOutsideAssembly).ToList();
            Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("NoAsmdef/Loose.cs"));
            Assert.Contains(issues, i => i.Severity == IssueSeverity.Info && i.Message.Contains("Assembly-CSharp-Editor"));
        }

        [Fact]
        public void Scripts_under_an_asmdef_are_compiled()
        {
            using var project = Simple("com.test.ok", "Com.Test.Ok", "namespace A { class X { } }");
            project.File("Packages/com.test.ok/Runtime/Deeper/More.cs", "namespace A { class Y { } }");
            None(project.Analyze(), IssueCodes.ScriptsOutsideAssembly);
        }

        [Fact]
        public void Url_dependency_in_package_json_is_an_error()
        {
            using var project = new AnalyzerProject();
            project.Package("com.test.dup", """{ "name": "com.test.dup", "version": "0.1.0", "dependencies": { "com.example.gitdep": "https://github.com/example/gitdep.git" } }""");
            var issue = Single(project.Analyze(), IssueCodes.DependencyUrl);
            Assert.Equal(IssueSeverity.Error, issue.Severity);
            Assert.Equal("com.example.gitdep", issue.Other);
        }

        [Fact]
        public void Range_dependency_version_is_an_error()
        {
            using var project = new AnalyzerProject();
            project.Package("com.test.a", """{ "name": "com.test.a", "version": "0.1.0", "dependencies": { "com.unity.mathematics": "^1.2.0" } }""");
            Assert.Equal(IssueSeverity.Error, Single(project.Analyze(), IssueCodes.DependencyVersionInvalid).Severity);
        }

        [Fact]
        public void Missing_dependency_is_an_error_unless_it_can_be_installed()
        {
            using var project = new AnalyzerProject();
            project.Package("com.test.bad", """{ "name": "com.test.bad", "version": "1.0.0", "dependencies": { "com.test.missing": "1.2.3" } }""");
            var issue = Single(project.Analyze(), IssueCodes.DependencyMissing);
            Assert.Equal(IssueSeverity.Error, issue.Severity);
            Assert.Contains("every package", issue.Message);

            var installable = project.Analyze(installable: new[] { "com.test.missing" });
            None(installable, IssueCodes.DependencyMissing);
            Assert.Equal(IssueSeverity.Info, Single(installable, IssueCodes.DependencyInstall).Severity);
        }

        [Fact]
        public void Unity_and_scoped_registry_dependencies_are_left_to_Unity()
        {
            using var project = new AnalyzerProject();
            project.RawManifest("""{ "dependencies": {}, "scopedRegistries": [ { "name": "OpenUPM", "url": "https://package.openupm.com", "scopes": [ "com.cysharp" ] } ] }""");
            project.Package("com.test.a", """{ "name": "com.test.a", "version": "1.0.0", "dependencies": { "com.unity.mathematics": "1.2.6", "com.cysharp.unitask": "2.5.0" } }""");
            var issues = project.Analyze();
            None(issues, IssueCodes.DependencyMissing);
            None(issues, IssueCodes.DependencyVersionInvalid);
        }

        [Fact]
        public void Installed_dependency_that_is_too_old_is_a_warning()
        {
            using var project = new AnalyzerProject();
            project.Package("com.test.base", """{ "name": "com.test.base", "version": "1.0.0" }""");
            project.Package("com.test.user", """{ "name": "com.test.user", "version": "1.0.0", "dependencies": { "com.test.base": "2.0.0" } }""");
            var issue = Single(project.Analyze(), IssueCodes.DependencyTooOld);
            Assert.Equal("com.test.user", issue.Package);
            Assert.Equal(IssueSeverity.Warning, issue.Severity);
        }

        [Fact]
        public void Installed_dependency_that_is_new_enough_is_fine()
        {
            using var project = new AnalyzerProject();
            project.Package("com.test.base", """{ "name": "com.test.base", "version": "2.1.0" }""");
            project.Package("com.test.user", """{ "name": "com.test.user", "version": "1.0.0", "dependencies": { "com.test.base": "2.0.0" } }""");
            var issues = project.Analyze();
            None(issues, IssueCodes.DependencyTooOld);
            None(issues, IssueCodes.DependencyMissing);
        }

        [Fact]
        public void Missing_vpm_dependency_is_a_warning()
        {
            using var project = new AnalyzerProject();
            project.Package("com.test.a", """{ "name": "com.test.a", "version": "1.0.0", "vpmDependencies": { "com.test.other": ">=1.0.0" } }""");
            Assert.Equal(IssueSeverity.Warning, Single(project.Analyze(), IssueCodes.VpmDependencyMissing).Severity);
            Assert.Equal(IssueSeverity.Info, Single(project.Analyze(installable: new[] { "com.test.other" }), IssueCodes.DependencyInstall).Severity);
        }

        [Fact]
        public void Reference_to_a_missing_assembly_is_a_warning()
        {
            using var project = Simple("com.test.bad", "Com.Test.Bad", "namespace A { class X { } }", "\"references\": [\"Some.Missing.Assembly\"]");
            var issue = Single(project.Analyze(), IssueCodes.AssemblyReferenceMissing);
            Assert.Equal(IssueSeverity.Warning, issue.Severity);
            Assert.Contains("Some.Missing.Assembly", issue.Message);
        }

        [Fact]
        public void Optional_reference_with_a_version_define_is_info()
        {
            using var project = Simple("com.test.opt", "Com.Test.Opt", "namespace A { class X { } }",
                "\"references\": [\"Optional.Assembly\"], \"versionDefines\": [ { \"name\": \"com.optional.package\", \"expression\": \"\", \"define\": \"HAS_OPTIONAL\" } ]");
            Assert.Equal(IssueSeverity.Info, Single(project.Analyze(), IssueCodes.AssemblyReferenceMissing).Severity);
        }

        [Fact]
        public void References_by_name_and_guid_resolve()
        {
            using var project = Simple("com.test.base", "Com.Test.Base", "namespace Base.Api { public class Thing { } }");
            project.Meta("Packages/com.test.base/Runtime/Com.Test.Base.asmdef", "0123456789abcdef0123456789abcdef");
            var user = project.Package("com.test.user");
            project.Asmdef(user + "/Runtime", "Com.Test.User", "\"references\": [\"GUID:0123456789abcdef0123456789abcdef\", \"Com.Test.Base\"]");
            project.File(user + "/Runtime/User.cs", "using Base.Api;\nnamespace User { class U { Thing t; } }");
            var issues = project.Analyze();
            None(issues, IssueCodes.AssemblyReferenceMissing);
            None(issues, IssueCodes.NamespaceNotReferenced);
            None(issues, IssueCodes.NamespaceMissing);
        }

        [Fact]
        public void Using_a_namespace_nothing_provides_is_an_error()
        {
            using var project = Simple("com.test.user", "Com.Test.User", "using Basis.Scripts.Missing;\nusing UnityEngine;\nusing System.Collections.Generic;\nnamespace User { class U { } }");
            var issue = Single(project.Analyze(), IssueCodes.NamespaceMissing);
            Assert.Equal(IssueSeverity.Error, issue.Severity);
            Assert.Contains("Basis.Scripts.Missing", issue.Message);
            Assert.Equal(1, issue.Line);
        }

        [Fact]
        public void Conditional_and_static_usings_are_not_checked()
        {
            using var project = Simple("com.test.user", "Com.Test.User", "#if SOMETHING\nusing Not.There;\n#endif\nusing static Also.Not.There;\nnamespace User { class U { } }");
            None(project.Analyze(), IssueCodes.NamespaceMissing);
        }

        [Fact]
        public void Using_a_namespace_from_an_unreferenced_assembly_is_an_error()
        {
            using var project = Simple("com.test.base", "Com.Test.Base", "namespace Base.Api { public class Thing { } }");
            var user = project.Package("com.test.user");
            project.Asmdef(user + "/Runtime", "Com.Test.User");
            project.File(user + "/Runtime/User.cs", "using Base.Api;\nnamespace User { class U { } }");
            var issue = Single(project.Analyze("com.test.user"), IssueCodes.NamespaceNotReferenced);
            Assert.Equal(IssueSeverity.Error, issue.Severity);
            Assert.Contains("Com.Test.Base", issue.Fix);
        }

        [Fact]
        public void Using_inside_a_namespace_resolves_relative_to_it()
        {
            using var project = Simple("com.test.user", "Com.Test.User", "namespace Company.Product { using Networking; class U { } }\nnamespace Company.Product.Networking { public class N { } }");
            None(project.Analyze(), IssueCodes.NamespaceMissing);
        }

        [Fact]
        public void Alias_to_a_nested_type_resolves()
        {
            using var project = Simple("com.test.user", "Com.Test.User", "using Kind = Holder.Inner;\npublic class Holder { public enum Inner { A } }\nnamespace User { class U { } }");
            None(project.Analyze(), IssueCodes.NamespaceMissing);
        }

        [Fact]
        public void UnityEngine_UI_is_implicit_in_Unity_6()
        {
            using var project = Simple("com.test.user", "Com.Test.User", "using UnityEngine.UI;\nnamespace User { class U { } }");
            var ugui = project.Package("com.unity.ugui");
            project.Asmdef(ugui + "/Runtime", "UnityEngine.UI");
            project.File(ugui + "/Runtime/Button.cs", "namespace UnityEngine.UI { public class Button { } }");
            var issues = project.Analyze("com.test.user", editor: EditorWithNoDlls());
            None(issues, IssueCodes.NamespaceNotReferenced);
        }

        [Fact]
        public void UnityEngine_UI_needs_a_reference_before_Unity_6()
        {
            using var project = new AnalyzerProject("2022.3.20f1");
            var user = project.Package("com.test.user");
            project.Asmdef(user + "/Runtime", "Com.Test.User");
            project.File(user + "/Runtime/User.cs", "using UnityEngine.UI;\nnamespace User { class U { } }");
            var ugui = project.Package("com.unity.ugui");
            project.Asmdef(ugui + "/Runtime", "UnityEngine.UI");
            project.File(ugui + "/Runtime/Button.cs", "namespace UnityEngine.UI { public class Button { } }");
            Assert.Equal(IssueSeverity.Error, Single(project.Analyze("com.test.user", editor: EditorWithNoDlls()), IssueCodes.NamespaceNotReferenced).Severity);
        }

        private static string EditorWithNoDlls()
        {
            var folder = Path.Combine(Path.GetTempPath(), "basispm-tests", "empty-editor", "Data", "Managed");
            Directory.CreateDirectory(folder);
            return Path.GetDirectoryName(Path.GetDirectoryName(folder))!;
        }

        [Fact]
        public void Editor_namespaces_come_from_editor_dlls_including_internal_only_ones()
        {
            using var project = Simple("com.test.user", "Com.Test.User", "using BasisPM.Core.Tests.InternalOnly;\nusing BasisPM.Core.Tests.Missing;\nnamespace User { class U { } }");
            var editor = Path.Combine(Path.GetTempPath(), "basispm-tests", "editor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(editor, "Data", "Managed"));
            File.Copy(typeof(PackageAnalyzerTests).Assembly.Location, Path.Combine(editor, "Data", "Managed", "Probe.dll"));
            try
            {
                var issue = Single(project.Analyze(editor: editor), IssueCodes.NamespaceMissing);
                Assert.Contains("BasisPM.Core.Tests.Missing", issue.Message);
            }
            finally { Directory.Delete(editor, true); }
        }

        [Fact]
        public void Type_named_like_a_namespace_elsewhere_clashes()
        {
            using var project = Simple("com.test.a", "Com.Test.A", "namespace Foo { public class Bar { } }");
            var b = project.Package("com.test.b");
            project.Asmdef(b + "/Runtime", "Com.Test.B");
            project.File(b + "/Runtime/B.cs", "namespace Foo.Bar { public class Baz { } }");
            Assert.Equal(IssueSeverity.Warning, Single(project.Analyze(), IssueCodes.NamespaceTypeClash).Severity);
        }

        [Fact]
        public void Distinct_names_do_not_clash()
        {
            using var project = Simple("com.test.a", "Com.Test.A", "namespace Foo { public class Bar { } }\nnamespace Foo.Other { public class Baz { } }");
            None(project.Analyze(), IssueCodes.NamespaceTypeClash);
        }

        [Fact]
        public void Global_namespace_types_are_a_best_practice_note()
        {
            using var project = Simple("com.test.a", "Com.Test.A", "public class MyHelper { }");
            Assert.Equal(IssueSeverity.Info, Single(project.Analyze(), IssueCodes.GlobalNamespace).Severity);
            using var clean = Simple("com.test.b", "Com.Test.B", "namespace Mine { public class MyHelper { } }");
            None(clean.Analyze(), IssueCodes.GlobalNamespace);
        }

        [Fact]
        public void Types_in_unity_or_basis_namespaces_are_noted()
        {
            using var project = Simple("com.test.a", "Com.Test.A", "namespace UnityEngine.Custom { public class Thing { } }\nnamespace Basis.Extra { public class More { } }");
            var issues = With(project.Analyze(), IssueCodes.ForeignNamespace).ToList();
            Assert.Equal(2, issues.Count);
            Assert.All(issues, i => Assert.Equal(IssueSeverity.Info, i.Severity));
        }

        [Fact]
        public void Basis_packages_may_use_the_Basis_namespace()
        {
            using var project = Simple("com.basis.extra", "Basis.Extra", "namespace Basis.Extra { public class More { } }");
            None(project.Analyze(), IssueCodes.ForeignNamespace);
        }

        [Fact]
        public void Editor_api_in_a_runtime_assembly_breaks_player_builds()
        {
            using var project = Simple("com.test.bad", "Com.Test.Bad", "using UnityEditor;\nnamespace A { public class P { void M() { AssetDatabase.Refresh(); } } }");
            var issue = Single(project.Analyze(), IssueCodes.EditorCodeInRuntime);
            Assert.Equal(IssueSeverity.Error, issue.Severity);
            Assert.Equal(2, issue.Line);
            Assert.Contains("AssetDatabase", issue.Message);
        }

        [Fact]
        public void Guarded_or_editor_only_or_unused_editor_code_is_fine()
        {
            using var guarded = Simple("com.test.a", "Com.Test.A", "#if UNITY_EDITOR\nusing UnityEditor;\n#endif\nnamespace A { public class P {\n#if UNITY_EDITOR\nvoid M() { AssetDatabase.Refresh(); }\n#endif\n} }");
            None(guarded.Analyze(), IssueCodes.EditorCodeInRuntime);
            using var editorOnly = Simple("com.test.b", "Com.Test.B", "using UnityEditor;\nnamespace B { public class P { void M() { AssetDatabase.Refresh(); } } }", "\"includePlatforms\": [\"Editor\"]");
            None(editorOnly.Analyze(), IssueCodes.EditorCodeInRuntime);
            using var unused = Simple("com.test.c", "Com.Test.C", "using UnityEditor;\nusing UnityEngine;\nnamespace C { public class P { } }");
            None(unused.Analyze(), IssueCodes.EditorCodeInRuntime);
            using var own = Simple("com.test.d", "Com.Test.D", "#if UNITY_EDITOR\nusing UnityEditor;\n#endif\nnamespace D { public class SerializedObject { public static void Flush() { } } public class P { void M() { SerializedObject.Flush(); } } }");
            None(own.Analyze(), IssueCodes.EditorCodeInRuntime);
        }

        [Fact]
        public void Visible_samples_folder_is_a_warning()
        {
            using var project = Simple("com.test.a", "Com.Test.A", "namespace A { class X { } }");
            project.File("Packages/com.test.a/Samples/Demo/Demo.cs", "class Demo { }");
            Assert.Equal(IssueSeverity.Warning, Single(project.Analyze(), IssueCodes.SamplesVisible).Severity);
            using var hidden = Simple("com.test.b", "Com.Test.B", "namespace B { class X { } }");
            hidden.File("Packages/com.test.b/Samples~/Demo/Demo.cs", "class Demo { }");
            None(hidden.Analyze(), IssueCodes.SamplesVisible);
        }

        [Fact]
        public void Test_assembly_without_include_tests_constraint_is_a_warning()
        {
            using var project = Simple("com.test.a", "Com.Test.A.Tests", "namespace A { class X { } }", "\"references\": [\"UnityEngine.TestRunner\"]");
            Assert.Equal(IssueSeverity.Warning, Single(project.Analyze(), IssueCodes.TestsNotConstrained).Severity);
            using var constrained = Simple("com.test.b", "Com.Test.B.Tests", "namespace B { class X { } }", "\"references\": [\"UnityEngine.TestRunner\"], \"defineConstraints\": [\"UNITY_INCLUDE_TESTS\"]");
            None(constrained.Analyze(), IssueCodes.TestsNotConstrained);
        }

        [Fact]
        public void Missing_metas_are_info_in_a_local_package()
        {
            using var project = Simple("com.test.a", "Com.Test.A", "namespace A { class X { } }");
            Assert.Equal(IssueSeverity.Info, Single(project.Analyze(), IssueCodes.MetaMissing).Severity);
            project.Metas("Packages/com.test.a");
            None(project.Analyze(), IssueCodes.MetaMissing);
        }

        [Fact]
        public void Missing_metas_are_a_warning_in_an_immutable_package()
        {
            using var project = new AnalyzerProject();
            project.Manifest(("com.test.cached", "https://example.com/cached.git"));
            project.File("Library/PackageCache/com.test.cached@abc123/package.json", """{ "name": "com.test.cached", "version": "1.0.0", "displayName": "Cached", "description": "d", "license": "MIT" }""");
            project.File("Library/PackageCache/com.test.cached@abc123/README.md", "x");
            var inventory = project.Scan();
            Assert.Equal(PackageOrigin.Cache, inventory.Find("com.test.cached")!.Origin);
            var issues = new PackageAnalyzer().Analyze(inventory);
            Assert.Equal(IssueSeverity.Warning, Single(issues, IssueCodes.MetaMissing).Severity);
        }

        [Fact]
        public void Generated_folders_are_junk_only_where_they_are_generated()
        {
            using var project = Simple("com.test.a", "Com.Test.A", "namespace A { class X { } }");
            project.File("Packages/com.test.a/Library/ArtifactDB", "x");
            project.File("Packages/com.test.a/UI/Library/Menu.cs", "namespace A { class Menu { } }");
            project.File("Packages/com.test.a/Tools/Thing.csproj", "<Project />");
            project.File("Packages/com.test.a/Tools/obj/project.assets.json", "{}");
            var issue = Single(project.Analyze(), IssueCodes.JunkFolder);
            Assert.Contains("Library", issue.Message);
            Assert.Contains("Tools/obj", issue.Message);
            Assert.DoesNotContain("UI/Library", issue.Message);
        }

        [Fact]
        public void Invalid_package_json_is_an_error()
        {
            using var project = new AnalyzerProject();
            project.Package("com.test.a", "{ \"name\": \"com.test.a\", ");
            Assert.Equal(IssueSeverity.Error, Single(project.Analyze(), IssueCodes.ManifestInvalid).Severity);
        }

        [Fact]
        public void Package_json_fields_follow_unitys_rules()
        {
            using var project = new AnalyzerProject();
            project.Package("com.test.a", """{ "name": "com.Test.A", "version": "1.0" }""", readme: false);
            var issues = project.Analyze();
            Assert.Equal(IssueSeverity.Warning, Single(issues, IssueCodes.NameInvalid).Severity);
            Assert.Equal(IssueSeverity.Error, Single(issues, IssueCodes.VersionInvalid).Severity);
            Assert.Equal(IssueSeverity.Info, Single(issues, IssueCodes.DisplayNameMissing).Severity);
            Assert.Equal(IssueSeverity.Info, Single(issues, IssueCodes.DescriptionMissing).Severity);
            Assert.Equal(IssueSeverity.Info, Single(issues, IssueCodes.LicenseMissing).Severity);
            Assert.Equal(IssueSeverity.Info, Single(issues, IssueCodes.ReadmeMissing).Severity);
        }

        [Fact]
        public void Complete_package_json_has_no_manifest_issues()
        {
            using var project = new AnalyzerProject();
            project.Package("com.test.a");
            var issues = project.Analyze();
            foreach (var code in new[] { IssueCodes.NameInvalid, IssueCodes.VersionInvalid, IssueCodes.DisplayNameMissing, IssueCodes.DescriptionMissing, IssueCodes.LicenseMissing, IssueCodes.ReadmeMissing })
                None(issues, code);
        }

        [Fact]
        public void Missing_name_or_version_is_an_error()
        {
            using var project = new AnalyzerProject();
            project.Package("com.test.a", """{ "displayName": "A" }""");
            var issues = project.Analyze();
            Assert.Equal(IssueSeverity.Error, Single(issues, IssueCodes.NameInvalid).Severity);
            Assert.Equal(IssueSeverity.Error, Single(issues, IssueCodes.VersionInvalid).Severity);
        }

        [Fact]
        public void Package_that_needs_a_newer_unity_is_a_warning()
        {
            using var project = new AnalyzerProject();
            project.Package("com.test.a", """{ "name": "com.test.a", "version": "1.0.0", "unity": "6000.9" }""");
            Assert.Equal(IssueSeverity.Warning, Single(project.Analyze(), IssueCodes.UnityNewer).Severity);
            using var older = new AnalyzerProject();
            older.Package("com.test.b", """{ "name": "com.test.b", "version": "1.0.0", "unity": "6000.0", "unityRelease": "0f1" }""");
            var issues = older.Analyze();
            None(issues, IssueCodes.UnityNewer);
            None(issues, IssueCodes.UnityInvalid);
        }

        [Fact]
        public void Malformed_unity_field_is_a_warning()
        {
            using var project = new AnalyzerProject();
            project.Package("com.test.a", """{ "name": "com.test.a", "version": "1.0.0", "unity": "6" }""");
            Assert.Equal(IssueSeverity.Warning, Single(project.Analyze(), IssueCodes.UnityInvalid).Severity);
        }

        [Fact]
        public void Two_embedded_folders_with_one_package_name_are_an_error()
        {
            using var project = new AnalyzerProject();
            project.Package("com.test.a", folder: "First");
            project.Package("com.test.a", folder: "Second");
            var issue = Single(project.Analyze(), IssueCodes.PackageDuplicate);
            Assert.Equal(IssueSeverity.Error, issue.Severity);
            Assert.Contains("Packages/First", issue.Message);
            Assert.Contains("Packages/Second", issue.Message);
        }

        [Fact]
        public void Broken_asmdef_or_two_in_one_folder_is_an_error()
        {
            using var project = Simple("com.test.a", "Com.Test.A", "namespace A { class X { } }");
            project.File("Packages/com.test.a/Runtime/Second.asmdef", "{ \"name\": \"Com.Test.Second\" }");
            project.File("Packages/com.test.a/Broken/Broken.asmdef", "{ not json");
            var issues = With(project.Analyze(), IssueCodes.AsmdefInvalid).ToList();
            Assert.Equal(2, issues.Count);
            Assert.All(issues, i => Assert.Equal(IssueSeverity.Error, i.Severity));
        }

        [Fact]
        public void Same_dll_in_two_packages_is_reported_with_which_copy_wins()
        {
            using var project = new AnalyzerProject();
            project.Package("com.test.bad");
            project.Package("com.test.dup");
            var dll = typeof(PackageAnalyzer).Assembly.Location;
            project.Copy(dll, "Packages/com.test.bad/Plugins/Shared.dll");
            project.Copy(dll, "Packages/com.test.dup/Plugins/Shared.dll");
            var issue = Single(project.Analyze(), IssueCodes.DllDuplicate);
            Assert.Equal(IssueSeverity.Warning, issue.Severity);
            Assert.Contains("Packages/com.test.bad/Plugins/Shared.dll", issue.Message);
            Assert.DoesNotContain("differ", issue.Message);
            Assert.DoesNotContain(project.Analyze(), i => i.Code == IssueCodes.TypeDuplicate);
        }

        [Fact]
        public void Differing_dll_copies_are_called_out()
        {
            using var project = new AnalyzerProject();
            project.Package("com.test.bad");
            project.Package("com.test.dup");
            project.Copy(typeof(PackageAnalyzer).Assembly.Location, "Packages/com.test.bad/Plugins/Shared.dll");
            project.Copy(typeof(Assert).Assembly.Location, "Packages/com.test.dup/Plugins/Shared.dll");
            Assert.Contains("differ", Single(project.Analyze(), IssueCodes.DllDuplicate).Message);
        }

        [Fact]
        public void Platform_copies_inside_one_package_are_not_duplicates()
        {
            using var project = new AnalyzerProject();
            project.Package("com.test.a");
            project.Copy(typeof(PackageAnalyzer).Assembly.Location, "Packages/com.test.a/Plugins/x64/Lib.dll");
            project.Copy(typeof(PackageAnalyzer).Assembly.Location, "Packages/com.test.a/Plugins/arm64/Lib.dll");
            var issues = project.Analyze();
            None(issues, IssueCodes.DllDuplicate);
            None(issues, IssueCodes.TypeDuplicate);
        }

        [Fact]
        public void Shared_meta_guid_across_packages_is_an_error()
        {
            using var project = new AnalyzerProject();
            project.Package("com.test.a");
            project.Package("com.test.b");
            project.File("Packages/com.test.a/Thing.asset", "x");
            project.File("Packages/com.test.b/Copy.asset", "x");
            project.Meta("Packages/com.test.a/Thing.asset", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            project.Meta("Packages/com.test.b/Copy.asset", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var issue = Assert.Single(project.Analyze("com.test.b"), i => i.Code == IssueCodes.GuidDuplicate);
            Assert.Equal(IssueSeverity.Error, issue.Severity);
            Assert.Equal("com.test.a", issue.Other);
        }

        [Fact]
        public void Distinct_guids_are_fine()
        {
            using var project = new AnalyzerProject();
            project.Package("com.test.a");
            project.Package("com.test.b");
            project.Metas("Packages");
            None(project.Analyze(), IssueCodes.GuidDuplicate);
        }

        [Fact]
        public async Task Check_package_inspects_a_candidate_outside_the_project()
        {
            using var project = Simple("com.test.installed", "Com.Test.Shared", "namespace Installed { public class A { } }");
            var candidate = project.Outside("candidate");
            Directory.CreateDirectory(Path.Combine(candidate, "Runtime"));
            File.WriteAllText(Path.Combine(candidate, "package.json"), """{ "name": "com.test.candidate", "version": "1.0.0", "displayName": "C", "description": "d", "license": "MIT" }""");
            File.WriteAllText(Path.Combine(candidate, "Runtime", "Com.Test.Shared.asmdef"), "{ \"name\": \"Com.Test.Shared\" }");
            File.WriteAllText(Path.Combine(candidate, "Runtime", "Debug.cs"), "public class Debug { }");
            var issues = await new PackageAnalyzer().CheckPackageAsync(project.Root, candidate);
            Assert.All(issues, i => Assert.Equal("com.test.candidate", i.Package));
            Assert.Contains(issues, i => i.Code == IssueCodes.AssemblyDuplicate && i.Severity == IssueSeverity.Error);
            Assert.Contains(issues, i => i.Code == IssueCodes.GlobalTypeShadows && i.Severity == IssueSeverity.Error);
        }

        [Fact]
        public async Task Check_package_reports_a_folder_without_package_json()
        {
            using var project = new AnalyzerProject();
            var candidate = project.Outside("notapackage");
            Directory.CreateDirectory(Path.Combine(candidate, "Assets"));
            var issue = Assert.Single(await new PackageAnalyzer().CheckPackageAsync(project.Root, candidate, "com.test.np"), i => i.Code == IssueCodes.NotAPackage);
            Assert.Equal(IssueSeverity.Error, issue.Severity);
        }

        [Fact]
        public async Task Check_package_with_a_different_id_reports_a_name_mismatch()
        {
            using var project = new AnalyzerProject();
            var candidate = project.Outside("renamed");
            Directory.CreateDirectory(candidate);
            File.WriteAllText(Path.Combine(candidate, "package.json"), """{ "name": "com.real.name", "version": "1.0.0" }""");
            Assert.Single(await new PackageAnalyzer().CheckPackageAsync(project.Root, candidate, "com.registry.id"), i => i.Code == IssueCodes.NameMismatch);
        }

        [Fact]
        public void Default_focus_skips_unity_packages_and_assets()
        {
            using var project = new AnalyzerProject();
            project.Package("com.unity.something", """{ "name": "com.unity.something", "version": "1.0" }""");
            project.File("Assets/Debug.cs", "public class Debug { }");
            var issues = project.Analyze();
            Assert.DoesNotContain(issues, i => i.Package is "com.unity.something" or "Assets");
        }

        [Fact]
        public void Scans_project_parts_and_classifies_sources()
        {
            using var project = Simple("com.test.a", "Com.Test.A", "namespace A { public class X { } }");
            project.File("Assets/Scripts/Game.cs", "public class Game { }");
            project.File("Assets/Editor/Tool.cs", "public class Tool { }");
            project.File(".basisdev/com.test.local/package.json", """{ "name": "com.test.local", "version": "1.0.0" }""");
            project.Manifest(("com.test.local", "file:../.basisdev/com.test.local"));
            var inventory = project.Scan();
            Assert.Equal(PackageOrigin.Local, inventory.Find("com.test.local")!.Origin);
            var assets = inventory.Find(UnityPackageMap.AssetsId)!;
            Assert.Equal("Assembly-CSharp", assets.Sources.Single(s => s.RelativePath == "Scripts/Game.cs").Assembly);
            Assert.Equal("Assembly-CSharp-Editor", assets.Sources.Single(s => s.RelativePath == "Editor/Tool.cs").Assembly);
            Assert.Equal("Com.Test.A", inventory.Find("com.test.a")!.Sources.Single().Assembly);
            Assert.Equal("6000.7.0b4", inventory.UnityVersion);
        }
    }
}

namespace BasisPM.Core.Tests.InternalOnly
{
    internal sealed class OnlyInternal { }
}
