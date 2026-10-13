using System.Text;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class ApiUpgradeServiceTests
{
    private const string CreateAssetMessage = "'ProjectWindowUtil.CreateAssetWithContent(string, string, Texture2D, Action<int>)' is obsolete: 'CreateAssetWithContent(string, string, Texture2D, Action<int>) is obsolete. Use CreateAssetWithTextContent(string, string, Texture2D, Action<EntityId>) instead.'";

    private static CompileDiagnostic Obsolete(string message, string? file = null, int line = 1, int column = 1, string code = "CS0619") =>
        new(CompileDiagnosticKind.Compiler, code == "CS0618" ? IssueSeverity.Warning : IssueSeverity.Error, code, message) { File = file, Line = line, Column = column };

    [Theory]
    [InlineData(CreateAssetMessage, "CreateAssetWithContent", "CreateAssetWithTextContent")]
    [InlineData("'SVGImporter.GeneratePhysicsShape' is obsolete: 'SVGImporter.GeneratePhysicsShape is deprecated. Use SVGImporter.GeneratePhysicsOutline instead (UnityUpgradable) -> GeneratePhysicsOutline'", "GeneratePhysicsShape", "GeneratePhysicsOutline")]
    [InlineData("'Selection.activeInstanceID' is obsolete: 'Use activeEntityId instead.'", "activeInstanceID", "activeEntityId")]
    [InlineData("'Thing.objectInstanceId' is obsolete: 'objectInstanceId is deprecated, use objectEntityId instead.'", "objectInstanceId", "objectEntityId")]
    [InlineData("'Object.FindObjectOfType<T>()' is obsolete: 'Use Object.FindFirstObjectByType<T>() instead (UnityUpgradable) -> [UnityEngine.CoreModule] UnityEngine.Object.FindFirstObjectByType<T>()'", "FindObjectOfType", "FindFirstObjectByType")]
    public void Obsolete_errors_that_name_a_replacement_become_renames(string message, string from, string to)
        => Assert.Equal((from, to), ApiUpgradeService.RuleFor(Obsolete(message)));

    [Theory]
    [InlineData("'ProjectWindowUtil.IsFolder(int)' is obsolete: 'IsFolder(int instanceID) is deprecated. Use IsFolder(EntityId entityId) instead.'")]
    [InlineData("'TreeView' is obsolete: 'TreeView is now deprecated. You can likely now use TreeView<int> instead and not think more about it.'")]
    [InlineData("'UnityEditorInternal.CanAppendBuild' is obsolete: 'CanAppendBuild has been deprecated. Use UnityEditor.CanAppendBuild instead (UnityUpgradable) -> [UnityEditor] UnityEditor.CanAppendBuild'")]
    [InlineData("'Foo.unityObjectReferences' is obsolete: 'Use entityIDs instead, and call Resources.EntityIdToObject(entityId) if you need a Unity object.'")]
    [InlineData("'Foo.Bar()' is obsolete: 'Use Baz.Qux() instead.'")]
    [InlineData("'Foo.Bar()' is obsolete: 'This will be removed in a future version.'")]
    public void Signature_changes_moves_and_vague_messages_are_left_alone(string message)
        => Assert.Null(ApiUpgradeService.RuleFor(Obsolete(message)));

    [Fact]
    public void Only_obsolete_diagnostics_are_considered()
        => Assert.Null(ApiUpgradeService.RuleFor(new CompileDiagnostic(CompileDiagnosticKind.Compiler, IssueSeverity.Error, "CS0103", CreateAssetMessage)));

    private sealed class Clone : IDisposable
    {
        private readonly TempDir _temp = new("apiupgrade");

        public Clone()
        {
            Project = _temp.CreateDir("Proj");
            Folder = _temp.CreateDir("Proj/.basisdev/com.example.toon");
            GitSandbox.Run(Folder, "init", "-q", "-b", "main");
            GitSandbox.Run(Folder, "config", "core.autocrlf", "false");
        }

        public string Project { get; }
        public string Folder { get; }

        public string Write(string relative, byte[] content)
        {
            var path = Path.Combine(Folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return path;
        }

        public void Commit()
        {
            GitSandbox.Commit(Folder, "package");
            var remote = _temp.Combine("remote.git");
            GitSandbox.Run(_temp.Path, "init", "-q", "--bare", remote);
            GitSandbox.Run(Folder, "remote", "add", "origin", remote);
            GitSandbox.Run(Folder, "push", "-q", "origin", "main");
        }

        public void Dispose() => _temp.Dispose();
    }

    private static byte[] Source(string body) =>
        new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("// ")).Concat(new byte[] { 0x82, 0xA0, 0x82, 0xA2 }).Concat(Encoding.UTF8.GetBytes("\r\n" + body)).ToArray();

    [GitFact]
    public async Task An_upgrade_renames_only_the_failing_call_keeps_every_other_byte_and_does_not_count_as_an_edit()
    {
        using var clone = new Clone();
        var original = Source("class Importer\r\n{\r\n    string CreateAssetWithContent = \"CreateAssetWithContent\";\r\n    void Make()\r\n    {\r\n        ProjectWindowUtil.CreateAssetWithContent(path, text);\r\n    }\r\n}\r\n");
        var path = clone.Write("Editor/Importer.cs", original);
        clone.Commit();
        var diagnostics = new[]
        {
            Obsolete(CreateAssetMessage, ".basisdev/com.example.toon/Editor/Importer.cs", 7, 9),
            Obsolete(CreateAssetMessage, "Assets/Mine.cs", 3, 9),
        };

        var plan = ApiUpgradeService.Plan(diagnostics, clone.Project);
        var applied = ApiUpgradeService.Apply(plan, "6000.7.0b4");

        var edit = Assert.Single(applied);
        Assert.Equal("com.example.toon", edit.Package);
        Assert.Equal(7, edit.Line);
        var upgraded = File.ReadAllBytes(path);
        var expected = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(original).Replace("ProjectWindowUtil.CreateAssetWithContent(", "ProjectWindowUtil.CreateAssetWithTextContent("));
        Assert.Equal(expected, upgraded);
        Assert.Contains("string CreateAssetWithContent = \"CreateAssetWithContent\"", Encoding.Latin1.GetString(upgraded));

        var git = new GitService();
        var changes = await git.GetChangesAsync(clone.Folder);
        Assert.Single(changes);
        Assert.Empty(ApiUpgradeService.WithoutUpgrades(clone.Folder, changes));
        var record = ApiUpgradeService.Current(clone.Folder)!;
        Assert.Equal("6000.7.0b4", record.Unity);
        Assert.Equal("CreateAssetWithTextContent", Assert.Single(record.Changes).To);
        Assert.False(await new MountService(git, new UnityProjectService(), new MountRegistry(clone.Project)).HasLocalWorkAsync(clone.Folder));

        File.AppendAllText(path, "// mine\r\n");
        Assert.Single(ApiUpgradeService.WithoutUpgrades(clone.Folder, await git.GetChangesAsync(clone.Folder)));
        Assert.Null(ApiUpgradeService.Current(clone.Folder));
        Assert.True(await new MountService(git, new UnityProjectService(), new MountRegistry(clone.Project)).HasLocalWorkAsync(clone.Folder));
    }

    [GitFact]
    public void An_upgrade_that_still_fails_after_the_recheck_is_put_back_exactly()
    {
        using var clone = new Clone();
        var original = Source("class A\r\n{\r\n    void B() { ProjectWindowUtil.CreateAssetWithContent(p, t, null, id => Use(id)); }\r\n}\r\n");
        var path = clone.Write("Editor/A.cs", original);
        clone.Commit();
        var applied = ApiUpgradeService.Apply(ApiUpgradeService.Plan(new[] { Obsolete(CreateAssetMessage, ".basisdev/com.example.toon/Editor/A.cs", 4, 16) }, clone.Project), "6000.7.0b4");
        Assert.Single(applied);
        Assert.NotEqual(original, File.ReadAllBytes(path));

        var after = new[] { new CompileDiagnostic(CompileDiagnosticKind.Compiler, IssueSeverity.Error, "CS1503", "Argument 1: cannot convert from 'UnityEngine.EntityId' to 'int'") { File = ".basisdev/com.example.toon/Editor/A.cs", Line = 4, Column = 82 } };
        var kept = ApiUpgradeService.KeepWorking(applied, clone.Project, after);

        Assert.Empty(kept);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Null(ApiUpgradeService.Read(clone.Folder));
    }

    [GitFact]
    public void Reverting_leaves_files_the_user_changed_since_alone()
    {
        using var clone = new Clone();
        var original = Source("class A { void B() { ProjectWindowUtil.CreateAssetWithContent(p, t); } }\r\n");
        var path = clone.Write("Editor/A.cs", original);
        clone.Commit();
        ApiUpgradeService.Apply(ApiUpgradeService.Plan(new[] { Obsolete(CreateAssetMessage, Path.Combine(clone.Folder, "Editor", "A.cs"), 2, 22) }, clone.Project), null);
        var upgraded = File.ReadAllBytes(path);
        Assert.NotEqual(original, upgraded);

        Assert.Equal(1, ApiUpgradeService.RevertAll(clone.Folder));
        Assert.Equal(original, File.ReadAllBytes(path));

        ApiUpgradeService.Apply(ApiUpgradeService.Plan(new[] { Obsolete(CreateAssetMessage, Path.Combine(clone.Folder, "Editor", "A.cs"), 2, 22) }, clone.Project), null);
        File.AppendAllText(path, "// mine\r\n");
        var mine = File.ReadAllBytes(path);
        Assert.Equal(0, ApiUpgradeService.RevertAll(clone.Folder));
        Assert.Equal(mine, File.ReadAllBytes(path));
    }

    [Fact]
    public void Folders_that_are_not_clones_are_never_touched()
    {
        using var t = new TempDir("apiupgrade");
        var project = t.CreateDir("Proj");
        t.WriteFile("Proj/.basisdev/loose/Editor/A.cs", "class A { void B() { ProjectWindowUtil.CreateAssetWithContent(p, t); } }");
        t.WriteFile("Proj/Packages/com.basis.framework/Editor/A.cs", "class A { void B() { ProjectWindowUtil.CreateAssetWithContent(p, t); } }");

        var plan = ApiUpgradeService.Plan(new[]
        {
            Obsolete(CreateAssetMessage, ".basisdev/loose/Editor/A.cs", 1, 22),
            Obsolete(CreateAssetMessage, "Packages/com.basis.framework/Editor/A.cs", 1, 22),
            Obsolete(CreateAssetMessage, "../outside/A.cs", 1, 22),
        }, project);

        Assert.Empty(plan);
    }
}
