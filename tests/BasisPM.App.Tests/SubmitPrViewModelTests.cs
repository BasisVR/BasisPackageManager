using Avalonia.Headless.XUnit;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class SubmitPrViewModelTests
{
    private static readonly GitFileChange[] Changes =
    {
        new(" M", "Runtime/A.cs", GitChangeKind.Modified, false),
        new("??", "Runtime/New.cs", GitChangeKind.Untracked, false),
        new(" D", "Runtime/Old.cs", GitChangeKind.Deleted, false),
    };

    [AvaloniaFact]
    public void Lists_every_change_next_to_a_draft_ready_to_edit()
    {
        var vm = new SubmitPrViewModel(new FakeHost(), "com.basis.Shader Kit", Changes);

        Assert.Equal(new[] { "M", "A", "D" }, vm.Files.Select(f => f.KindLetter));
        Assert.Equal(new[] { "Runtime/A.cs", "Runtime/New.cs", "Runtime/Old.cs" }, vm.Files.Select(f => f.DisplayPath));
        Assert.True(vm.Files[0].IsModified);
        Assert.True(vm.Files[1].IsAdded);
        Assert.True(vm.Files[2].IsDeleted);
        Assert.Equal(L.Tr("dialog.submitPr.kind.added"), vm.Files[1].KindLabel);
        Assert.Equal(L.Tr("dialog.submitPr.files", 3), vm.FilesText);
        Assert.Equal(L.Tr("dialog.submitPr.defaultTitle", "com.basis.Shader Kit"), vm.Title);
        Assert.Equal("basis-edit-com-basis-shader-kit", vm.Branch);
        Assert.False(vm.HasDiff);
        Assert.True(vm.IsIdle);
    }

    [AvaloniaFact]
    public void Selecting_a_file_shows_its_diff()
    {
        var host = new FakeHost();
        var vm = new SubmitPrViewModel(host, "com.basis.a", Changes);

        vm.SelectFile(vm.Files[1]);

        Assert.Same(vm.Files[1], vm.SelectedFile);
        Assert.True(vm.Files[1].IsSelected);
        Assert.Equal("Runtime/New.cs", vm.DiffTitle);
        Assert.Equal(new[] { "@@ -0,0 +1 @@", "+class    New {}" }, vm.DiffLines.Select(l => l.Text));
        Assert.True(vm.DiffLines[1].IsAdded);
        Assert.False(vm.IsDiffLoading);

        vm.SelectFile(vm.Files[0]);

        Assert.False(vm.Files[1].IsSelected);
        Assert.True(vm.Files[0].IsSelected);
        Assert.Equal(new[] { "Runtime/New.cs", "Runtime/A.cs" }, host.Diffed.Select(c => c.Path));
    }

    [AvaloniaFact]
    public async Task Sign_in_hands_back_the_trimmed_request_and_the_login()
    {
        var vm = new SubmitPrViewModel(new FakeHost(), "com.basis.a", Changes) { Title = "  Fix A  ", Body = "  Details  ", Branch = " fix-a " };

        var draft = await vm.SignInAsync();

        Assert.NotNull(draft);
        Assert.Equal(new PrRequest("Fix A", "Details", "fix-a", null), draft.Request);
        Assert.Equal("token", draft.Token);
        Assert.Equal("tester", draft.User.Login);
        Assert.False(vm.HasError);
        Assert.True(vm.IsIdle);
    }

    [AvaloniaFact]
    public async Task Without_a_token_it_asks_once_and_remembers_the_answer()
    {
        var host = new FakeHost { Token = null };
        var vm = new SubmitPrViewModel(host, "com.basis.a", Changes) { Body = " ", Branch = "" };
        var asked = 0;
        vm.AskForToken = () => { asked++; return Task.FromResult<string?>(" pat "); };

        var draft = await vm.SignInAsync();

        Assert.Equal(1, asked);
        Assert.Equal("pat", host.Token);
        Assert.Equal("pat", draft!.Token);
        Assert.Null(draft.Request.Body);
        Assert.Equal("basis-pm-edit", draft.Request.Branch);
    }

    [AvaloniaFact]
    public async Task A_cancelled_or_rejected_sign_in_stays_open_with_the_reason()
    {
        var cancelled = new SubmitPrViewModel(new FakeHost { Token = null }, "com.basis.a", Changes) { AskForToken = () => Task.FromResult<string?>(null) };
        Assert.Null(await cancelled.SignInAsync());
        Assert.Equal(L.Tr("develop.status.signInFirst"), cancelled.ErrorText);
        Assert.True(cancelled.IsIdle);

        var rejected = new SubmitPrViewModel(new FakeHost { User = null }, "com.basis.a", Changes);
        Assert.Null(await rejected.SignInAsync());
        Assert.Equal(L.Tr("develop.status.loginUnverified"), rejected.ErrorText);
    }

    [AvaloniaFact]
    public async Task A_blank_title_does_not_sign_in()
    {
        var host = new FakeHost();
        var vm = new SubmitPrViewModel(host, "com.basis.a", Changes) { Title = "  " };

        Assert.Null(await vm.SignInAsync());
        Assert.Equal(0, host.TokenRequests);
        Assert.False(vm.HasError);
    }

    private sealed class FakeHost : ISubmitPrHost
    {
        public string? Token { get; set; } = "token";
        public GitHubUser? User { get; init; } = new GitHubUser { Login = "tester", Id = 1 };
        public int TokenRequests { get; private set; }
        public List<GitFileChange> Diffed { get; } = new();

        public Task<IReadOnlyList<BasisDiffLine>> GetDiffAsync(GitFileChange change, CancellationToken ct)
        {
            Diffed.Add(change);
            return Task.FromResult<IReadOnlyList<BasisDiffLine>>(new[] { new BasisDiffLine(BasisDiffLineKind.Hunk, "@@ -0,0 +1 @@"), new BasisDiffLine(BasisDiffLineKind.Added, "+class\tNew {}") });
        }
        public Task<string?> GetTokenAsync()
        {
            TokenRequests++;
            return Task.FromResult(Token);
        }
        public void RememberToken(string token) => Token = token;
        public Task<GitHubUser?> GetUserAsync(string token) => Task.FromResult(User);
    }
}
