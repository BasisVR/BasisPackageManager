using Avalonia.Headless.XUnit;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class AuthoringViewModelTests
{
    private static readonly BasisAssembly[] Assemblies =
    {
        new("Basis Framework", "com.basis.framework", false),
        new("Basis Framework.Editor", "com.basis.framework", true),
        new("BasisSDK", "com.basis.sdk", false),
        new("BasisGizmos", "com.basis.gizmos", false),
    };

    private sealed class FakeHost : IPublishHost
    {
        public string? Token { get; set; } = "token";
        public IReadOnlyList<PackageIssue> Issues { get; set; } = Array.Empty<PackageIssue>();
        public string? Remembered { get; private set; }

        public Task<string?> GetTokenAsync() => Task.FromResult(Token);
        public void RememberToken(string token) => Token = Remembered = token;
        public Task<GitHubUser?> GetUserAsync(string token) => Task.FromResult<GitHubUser?>(new GitHubUser { Login = "tester", Id = 7 });
        public Task<IReadOnlyList<PackageIssue>> CheckAsync(string packageRoot, CancellationToken ct) => Task.FromResult(Issues);
    }

    private static NewPackageViewModel NewPackage(Func<string, string?>? conflict = null) =>
        new("Basis Tube", "Doola", "6000.2", "2026-09-16", Assemblies, serverAvailable: true, conflict ?? (_ => null));

    private static PublishPlan Plan(string current = "1.2.0", string? lastTag = "v1.2.0", string? branch = "main", UpmGitUrl? origin = null, int changed = 0) => new(
        "C:/p/.basisdev/com.studio.seats", "C:/p/.basisdev/com.studio.seats", "com.studio.seats", "Seats", current,
        lastTag is null ? current : PackagePublishService.NextVersion(current, VersionBump.Patch)!, lastTag, "v",
        new[] { new GitCommitInfo("abc1234567", "abc1234", "x", DateTimeOffset.UnixEpoch, "Fix seat height") }, branch,
        origin ?? UpmGitUrl.Parse("https://github.com/studio/seats.git"), changed);

    [AvaloniaFact]
    public void The_id_follows_the_name_until_it_is_edited()
    {
        var vm = NewPackage();

        vm.DisplayName = "Cool Seats";
        Assert.Equal("com.doola.cool-seats", vm.Id);
        Assert.Equal(L.Tr("dialog.newPackage.assemblyPreview", "Doola.CoolSeats"), vm.AssemblyPreview);
        Assert.Equal(L.Tr("dialog.newPackage.folderPreview", ".basisdev/com.doola.cool-seats"), vm.FolderPreview);

        vm.Id = "com.doola.chairs";
        vm.DisplayName = "Chairs";
        Assert.Equal("com.doola.chairs", vm.Id);

        Assert.Equal(new[] { "Basis Framework", "BasisSDK", "BasisGizmos" }, vm.Assemblies.Select(a => a.Name));
        Assert.Equal(new[] { true, true, false }, vm.Assemblies.Select(a => a.IsSelected));
        Assert.Equal(L.Tr("dialog.newPackage.basis", "2026-09-16"), vm.BasisText);
    }

    [AvaloniaFact]
    public void Creating_needs_a_name_a_valid_id_and_no_clash()
    {
        var vm = NewPackage(id => id == "com.doola.taken" ? "exists" : null);

        Assert.Null(vm.Build());
        Assert.Equal(L.Tr("dialog.newPackage.error.nameMissing"), vm.ErrorText);

        vm.DisplayName = "Seats";
        vm.Id = "Seats";
        Assert.Null(vm.Build());
        Assert.Equal(L.Tr("dialog.newPackage.error.idCharacters", "Seats"), vm.ErrorText);

        vm.Id = "com.doola.taken";
        Assert.Null(vm.Build());
        Assert.Equal(L.Tr("dialog.newPackage.error.exists", "com.doola.taken"), vm.ErrorText);

        vm.Id = "com.doola.seats";
        vm.Description = "  Chairs.  ";
        vm.Server = true;
        vm.Assemblies[2].IsSelected = true;
        vm.SelectedLicense = vm.Licenses[1];
        var draft = vm.Build()!;

        Assert.False(vm.HasError);
        Assert.Equal("com.doola.seats", draft.Request.Id);
        Assert.Equal("Chairs.", draft.Request.Description);
        Assert.Equal("6000.2", draft.Request.Unity);
        Assert.Equal("2026-09-16", draft.Request.BasisFrom);
        Assert.Equal(ScaffoldLicense.Apache2, draft.Request.License);
        Assert.Equal(new[] { "Basis Framework", "BasisSDK", "BasisGizmos" }, draft.Request.References);
        Assert.True(draft.Request.Server);
        Assert.False(draft.CreateRepository);
    }

    [AvaloniaFact]
    public async Task Putting_it_on_github_signs_in_first()
    {
        var host = new FakeHost { Token = null };
        var vm = NewPackage();
        vm.Host = host;
        vm.DisplayName = "Seats";
        vm.CreateRepository = true;

        Assert.Null(await vm.ConfirmAsync());
        Assert.Equal(L.Tr("develop.status.signInFirst"), vm.ErrorText);

        vm.AskForToken = () => Task.FromResult<string?>(" pat ");
        var draft = (await vm.ConfirmAsync())!;

        Assert.Equal("pat", host.Remembered);
        Assert.Equal("pat", draft.Token);
        Assert.Equal("tester", draft.User!.Login);
        Assert.True(draft.CreateRepository);
        Assert.True(vm.IsIdle);
    }

    [AvaloniaFact]
    public void Publishing_checks_the_version()
    {
        var vm = new PublishPackageViewModel(new FakeHost(), Plan(), new[] { "v1.2.0", "v1.2.1" }, inRegistry: true, new[] { "Misc" });

        Assert.Equal("1.2.1", vm.Version);
        Assert.Equal(L.Tr("dialog.publish.error.tagExists", "v1.2.1"), vm.VersionError);
        Assert.False(vm.CanPublish);

        vm.MinorCommand.Execute(null);
        Assert.Equal("1.3.0", vm.Version);
        Assert.False(vm.HasVersionError);
        Assert.Equal(L.Tr("dialog.publish.tagPreview", "v1.3.0"), vm.TagPreview);
        Assert.True(vm.CanPublish);

        vm.Version = "1.1.9";
        Assert.Equal(L.Tr("dialog.publish.error.notNewer", "v1.2.0"), vm.VersionError);
        vm.Version = "two";
        Assert.Equal(L.Tr("dialog.publish.error.version", "two"), vm.VersionError);
        vm.MajorCommand.Execute(null);
        Assert.Equal("2.0.0", vm.Version);
        Assert.Equal("- Fix seat height", vm.Notes);
        Assert.False(vm.CanSubmitToRegistry);
        Assert.False(vm.NeedsRepository);
    }

    [AvaloniaFact]
    public async Task Errors_from_the_checks_block_publishing_but_notes_do_not()
    {
        var host = new FakeHost { Issues = new[] { new PackageIssue(IssueCodes.ReadmeMissing, IssueSeverity.Warning, "com.studio.seats", "No README.") } };
        var vm = new PublishPackageViewModel(host, Plan(), Array.Empty<string>(), inRegistry: false, new[] { "Worlds", "Misc" });

        await vm.CheckAsync();
        Assert.True(vm.CanPublish);
        Assert.Equal(L.Tr("dialog.publish.checksWarnings", 1), vm.ChecksText);

        host.Issues = new[] { new PackageIssue(IssueCodes.VersionInvalid, IssueSeverity.Error, "com.studio.seats", "Bad version.") };
        await vm.CheckAsync();
        Assert.False(vm.CanPublish);
        Assert.Equal(L.Tr("dialog.publish.checksBlocked", 1), vm.ChecksText);
        Assert.Null(await vm.ConfirmAsync());
    }

    [AvaloniaFact]
    public async Task Confirming_hands_back_the_release_and_registry_choices()
    {
        var vm = new PublishPackageViewModel(new FakeHost(), Plan(origin: null, changed: 2) with { Origin = null }, Array.Empty<string>(), inRegistry: false, new[] { "Worlds", "Misc" })
        {
            Notes = "- Arm rests",
            SubmitToRegistry = true,
            Category = "Worlds",
            TagsText = "seats, furniture",
            RepositoryName = "seats",
        };

        Assert.True(vm.NeedsRepository);
        Assert.True(vm.HasChangedFiles);
        var draft = (await vm.ConfirmAsync())!;

        Assert.Equal(new PublishRequest("1.2.1", "- Arm rests", true), draft.Request);
        Assert.True(draft.CreateRepository);
        Assert.Equal("seats", draft.RepositoryName);
        Assert.True(draft.SubmitToRegistry);
        Assert.Equal("Worlds", draft.Category);
        Assert.Equal(new[] { "seats", "furniture" }, draft.Tags);
        Assert.Equal("tester", draft.User.Login);
    }

    [AvaloniaFact]
    public void A_detached_clone_cannot_be_published()
    {
        var vm = new PublishPackageViewModel(new FakeHost(), Plan(branch: null), Array.Empty<string>(), inRegistry: true, new[] { "Misc" });

        Assert.True(vm.IsDetached);
        Assert.False(vm.CanPublish);
    }

    [AvaloniaFact]
    public void Can_publish_is_observable_on_the_row()
    {
        var row = new PackageRow(new CatalogPackageVersion { Name = "com.studio.seats", DisplayName = "Seats" }, null, isMounted: true, mountFolder: "C:/x");
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.CanPublish = true;

        Assert.True(row.CanPublish);
        Assert.Contains(nameof(PackageRow.CanPublish), changed);
    }
}
