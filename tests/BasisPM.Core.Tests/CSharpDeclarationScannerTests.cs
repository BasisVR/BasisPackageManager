using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class CSharpDeclarationScannerTests
{
    private static CSharpTypeDeclaration Type(CSharpSourceInfo info, string name) => Assert.Single(info.Types, t => t.Name == name);

    [Fact]
    public void Finds_top_level_types_in_block_namespaces()
    {
        var info = CSharpDeclarationScanner.Scan("""
            namespace A.B
            {
                public class One { class Nested { } }
                internal struct Two { }
                public interface IThree<T, U> { }
                public enum Four : byte { X, Y }
                namespace C { public sealed partial class Five { } }
            }
            """);
        Assert.Equal(new[] { "One", "Two", "IThree", "Four", "Five" }, info.Types.Select(t => t.Name));
        Assert.Equal("A.B.One", Type(info, "One").FullName);
        Assert.False(Type(info, "Two").IsPublic);
        Assert.Equal(2, Type(info, "IThree").Arity);
        Assert.Equal("A.B.IThree`2", Type(info, "IThree").MetadataName);
        Assert.Equal("enum", Type(info, "Four").Kind);
        Assert.Equal("A.B.C.Five", Type(info, "Five").FullName);
        Assert.Contains("A", info.Namespaces);
        Assert.Contains("A.B", info.Namespaces);
        Assert.Contains("A.B.C", info.Namespaces);
    }

    [Fact]
    public void Nested_types_are_not_reported()
    {
        var info = CSharpDeclarationScanner.Scan("public class Outer { public class Inner { public struct Deep { } } enum E { } }");
        Assert.Equal("Outer", Assert.Single(info.Types).Name);
    }

    [Fact]
    public void File_scoped_namespace_applies_to_the_rest_of_the_file()
    {
        var info = CSharpDeclarationScanner.Scan("using System;\nnamespace Company.Product;\npublic record Point(int X, int Y);\npublic record struct Pair(int A);\npublic class Thing { }");
        Assert.Equal("Company.Product.Point", Type(info, "Point").FullName);
        Assert.Equal("record", Type(info, "Point").Kind);
        Assert.Equal("record struct", Type(info, "Pair").Kind);
        Assert.Equal("Company.Product.Thing", Type(info, "Thing").FullName);
    }

    [Fact]
    public void Global_namespace_types_have_an_empty_namespace()
    {
        var info = CSharpDeclarationScanner.Scan("public class Debug { }\nfile class Hidden { }");
        Assert.Equal("", Type(info, "Debug").Namespace);
        Assert.True(Type(info, "Hidden").IsFileLocal);
    }

    [Fact]
    public void Delegates_are_types_with_their_own_arity()
    {
        var info = CSharpDeclarationScanner.Scan("namespace N { public delegate void Handler<T>(T value); public delegate System.Threading.Tasks.Task<int> Worker(int x); }");
        Assert.Equal(1, Type(info, "Handler").Arity);
        Assert.Equal("delegate", Type(info, "Handler").Kind);
        Assert.Equal(0, Type(info, "Worker").Arity);
    }

    [Fact]
    public void Attributes_and_generic_constraints_do_not_confuse_declarations()
    {
        var info = CSharpDeclarationScanner.Scan("""
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Tests")]
            namespace N
            {
                [System.Serializable, Obsolete("use { other }")]
                public class Box<T> where T : class, new() { }
                [Flags] public enum Mask { A = 1 }
            }
            """);
        Assert.Equal(new[] { "Box", "Mask" }, info.Types.Select(t => t.Name));
        Assert.Equal("class", Type(info, "Box").Kind);
        Assert.Equal(1, Type(info, "Box").Arity);
    }

    [Fact]
    public void Braces_inside_strings_chars_and_comments_are_ignored()
    {
        var info = CSharpDeclarationScanner.Scan(""""
            namespace N
            {
                public class A
                {
                    string a = "}{"; string b = @"multi
            } line"; char c = '{'; char d = '\''; string e = $"x {(true ? "}" : "{")} y {{ }}";
                    // }
                    /* { */
                    string raw = """
                        }} {{
                        """;
                    string rawHole = $$"""{{ "}" }} """;
                }
                public class B { }
            }
            """");
        Assert.Equal(new[] { "A", "B" }, info.Types.Select(t => t.Name));
        Assert.Equal("N.B", Type(info, "B").FullName);
    }

    [Fact]
    public void Conditional_branches_with_unbalanced_braces_follow_the_first_branch()
    {
        var info = CSharpDeclarationScanner.Scan("""
            namespace N
            {
            #if UNITY_EDITOR
                public class Inspector : UnityEditor.Editor
            #else
                public class Inspector : UnityEngine.MonoBehaviour
            #endif
                {
            #if A
                    void M() {
            #else
                    void M2() {
            #endif
                    }
                }
                public class After { }
            }
            """);
        Assert.Single(info.Types, t => t.Name == "Inspector");
        var after = Type(info, "After");
        Assert.False(after.IsConditional);
        Assert.Equal("N.After", after.FullName);
    }

    [Fact]
    public void Types_declared_inside_one_branch_are_conditional()
    {
        var info = CSharpDeclarationScanner.Scan("""
            namespace N
            {
            #if UNITY_ANDROID
                public class OnlyAndroid { }
            #endif
                public class Always { }
            }
            """);
        Assert.True(Type(info, "OnlyAndroid").IsConditional);
        Assert.False(Type(info, "Always").IsConditional);
    }

    [Fact]
    public void Reads_using_directives()
    {
        var info = CSharpDeclarationScanner.Scan("""
            using System;
            using static System.Math;
            global using Basis.Scripts;
            using Alias = Some.Name.Space.Type<int>;
            #if FOO
            using Optional.Thing;
            #endif
            namespace Outer { using Inner; }
            """);
        var targets = info.Usings.Select(u => u.Target).ToList();
        Assert.Equal(new[] { "System", "System.Math", "Basis.Scripts", "Some.Name.Space.Type<int>", "Optional.Thing", "Inner" }, targets);
        Assert.True(info.Usings[1].IsStatic);
        Assert.True(info.Usings[2].IsGlobal);
        Assert.Equal("Alias", info.Usings[3].Alias);
        Assert.True(info.Usings[4].IsConditional);
        Assert.False(info.Usings[0].IsConditional);
        Assert.Equal("Outer", info.Usings[5].Scope);
        Assert.Equal("", info.Usings[0].Scope);
    }

    [Fact]
    public void Qualified_UnityEditor_use_outside_a_guard_is_reported()
    {
        var info = CSharpDeclarationScanner.Scan("public class A\n{\n    void M() { UnityEditor.AssetDatabase.Refresh(); }\n}");
        Assert.Equal(new[] { 3 }, info.UnguardedEditorLines);
    }

    [Fact]
    public void UnityEditor_inside_an_editor_guard_is_not_reported()
    {
        var info = CSharpDeclarationScanner.Scan("""
            #if UNITY_EDITOR
            using UnityEditor;
            #endif
            public class A
            {
            #if UNITY_EDITOR && !FOO
                void M() { UnityEditor.AssetDatabase.Refresh(); AssetDatabase.Refresh(); }
            #endif
            }
            """);
        Assert.Empty(info.UnguardedEditorLines);
        Assert.Empty(info.EditorTypeUses);
        Assert.Empty(info.UnguardedEditorUsings);
    }

    [Fact]
    public void The_else_branch_of_an_editor_guard_is_not_guarded()
    {
        var info = CSharpDeclarationScanner.Scan("#if UNITY_EDITOR\nclass A { }\n#else\nclass B { void M() { UnityEditor.EditorUtility.SetDirty(null); } }\n#endif\n#if !UNITY_EDITOR\nclass C { }\n#else\nclass D { void M() { UnityEditor.EditorUtility.SetDirty(null); } }\n#endif");
        Assert.Equal(new[] { 4 }, info.UnguardedEditorLines);
    }

    [Fact]
    public void Player_safe_editor_namespaces_are_not_reported()
    {
        var info = CSharpDeclarationScanner.Scan("class A { object a = UnityEditor.Experimental.RenderSettings.useRadianceAmbientProbe; }");
        Assert.Empty(info.UnguardedEditorLines);
    }

    [Fact]
    public void Editor_type_uses_need_a_UnityEditor_using_and_a_type_position()
    {
        var info = CSharpDeclarationScanner.Scan("""
            using UnityEditor;
            using UnityEngine;
            [InitializeOnLoad]
            public class A : Editor
            {
                void M()
                {
                    AssetDatabase.Refresh();
                    var o = new SerializedObject(this);
                    var s = new Settings { Handles = 1 };
                    s.Undo = 2;
                }
            }
            public class SerializedProperty { }
            """);
        var uses = info.EditorTypeUses.Select(u => (u.Line, u.Name)).ToList();
        Assert.Contains((3, "InitializeOnLoad"), uses);
        Assert.Contains((4, "Editor"), uses);
        Assert.Contains((8, "AssetDatabase"), uses);
        Assert.Contains((9, "SerializedObject"), uses);
        Assert.DoesNotContain(uses, u => u.Name is "Handles" or "Undo" or "SerializedProperty");
        Assert.Equal(new[] { 1 }, info.UnguardedEditorUsings);
    }

    [Fact]
    public void Editor_type_names_without_any_UnityEditor_using_are_ignored()
    {
        var info = CSharpDeclarationScanner.Scan("public class A { void M() { AssetDatabase.Refresh(); } }\npublic static class AssetDatabase { public static void Refresh() { } }");
        Assert.Empty(info.EditorTypeUses);
        Assert.Empty(info.UnguardedEditorLines);
    }

    [Fact]
    public void Namespace_named_UnityEditor_is_not_a_use()
    {
        var info = CSharpDeclarationScanner.Scan("namespace UnityEditor.MyTools { public class Tool { } }");
        Assert.Empty(info.UnguardedEditorLines);
        Assert.Equal("UnityEditor.MyTools.Tool", Type(info, "Tool").FullName);
    }

    [Fact]
    public void Empty_and_malformed_sources_do_not_throw()
    {
        Assert.Empty(CSharpDeclarationScanner.Scan("").Types);
        Assert.Empty(CSharpDeclarationScanner.Scan("}}}} namespace").Types);
        Assert.Single(CSharpDeclarationScanner.Scan("public class A { string s = \"unterminated\n }\npublic class B { }").Types, t => t.Name == "A");
    }

    [Fact]
    public void Line_numbers_point_at_the_type_name()
    {
        var info = CSharpDeclarationScanner.Scan("// header\n\nnamespace N\n{\n    public class\n        Late { }\n}");
        Assert.Equal(6, Type(info, "Late").Line);
    }
}
