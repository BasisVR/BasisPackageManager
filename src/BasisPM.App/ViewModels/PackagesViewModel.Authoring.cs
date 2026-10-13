using System.Globalization;
using BasisPM.App.Localization;
using BasisPM.App.Services;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed partial class PackagesViewModel
{
    private static readonly string[] DefaultRegistryCategories = { "Audio", "Avatars", "Developer Tools", "Examples", "Gameplay", "Integrations", "Interactions", "Media", "Networking", "Physics", "Props", "Rendering", "Shaders", "Tracking", "UI", "Worlds" };
    private static readonly string[] InternalRegistryCategories = { "Core", "Dependencies", "Platform", "Misc" };
    private PackageScaffoldService? _scaffoldService;
    private PackagePublishService? _publishService;
    private RelayCommand? _newPackageCommand;
    private RelayCommand<PackageRow>? _publishCommand;

    private Func<string, CancellationToken, Task<IReadOnlyList<PackageIssue>>>? _prePublishCheck;
    public Func<string, CancellationToken, Task<IReadOnlyList<PackageIssue>>> PrePublishCheck
    {
        get => _prePublishCheck ??= (root, ct) => PackagePublishService.FullChecksAsync(_install?.UnityProjectPath, root, ct);
        set => _prePublishCheck = value;
    }

    private PackageScaffoldService Scaffold => _scaffoldService ??= new PackageScaffoldService(_gitService, _projectService, _mountService);
    private PackagePublishService Publisher => _publishService ??= new PackagePublishService(_gitService, _ghApi, _mountService);

    public RelayCommand NewPackageCommand => _newPackageCommand ??= new RelayCommand(NewPackageAsync);
    public RelayCommand<PackageRow> PublishCommand => _publishCommand ??= new RelayCommand<PackageRow>(row =>
        row is null || _install is not { HasUnityProject: true } target ? Task.CompletedTask : PublishPackageAsync(target, row.Name, row.DisplayName));

    private async Task NewPackageAsync()
    {
        if (_install is not { HasUnityProject: true } target) { SetStatus(L.Tr("packages.status.chooseProject"), StatusKind.Error); return; }
        if (!_gitService.IsAvailable) { SetStatus(L.Tr("develop.status.gitRequired"), StatusKind.Error); return; }
        NewPackageDraft? draft;
        try
        {
            var author = await DefaultAuthorAsync(target);
            var basis = await Scaffold.ResolveBasisDateAsync(_basisUpdates, target);
            var assemblies = await Task.Run(() => PackageScaffoldService.FindBasisAssemblies(target.UnityProjectPath));
            var model = new NewPackageViewModel(target.DisplayName, author, PackageScaffoldService.UnityMajorMinor(target.UnityVersion), FormatDate(basis), assemblies,
                ServerPackageService.HasServer(target.RepoRoot), id => Scaffold.FindConflict(target, id))
            {
                Host = new PublishHost(_ghAuth, _ghApi, PrePublishCheck),
            };
            draft = await Dialogs.NewPackageAsync(model);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Opening the new package dialog", ex);
            SetStatus(L.Tr("authoring.status.createFailed", ex.Message), StatusKind.Error, target.DisplayName);
            return;
        }
        if (draft is not null) await CreatePackageAsync(target, draft);
    }

    private Task CreatePackageAsync(BasisInstall target, NewPackageDraft draft) =>
        PackageWorkAsync(target, draft.Request.Id, L.Tr("authoring.status.creating", draft.Request.DisplayName), async () =>
        {
            var key = QueueKey(target, draft.Request.Id);
            Task Retry() => CreatePackageAsync(target, draft);
            IsBusy = true;
            SetStatus(L.Tr("authoring.status.creating", draft.Request.DisplayName), StatusKind.Info, target.DisplayName);
            try
            {
                var result = await Scaffold.CreateAsync(target, draft.Request);
                if (!result.Ok)
                {
                    SetStatus(L.Tr("authoring.status.createFailed", result.Message), StatusKind.Error, target.DisplayName, result.Folder is null ? Retry : null, key);
                    return;
                }
                var where = BasisDevStore.FolderName + "/" + draft.Request.Id;
                if (draft.CreateRepository && draft.Token is { } token && result.Folder is { } folder)
                {
                    try
                    {
                        var url = await Publisher.CreateRepositoryAsync(target, draft.Request.Id, folder, token, PackageScaffoldService.Slug(draft.Request.DisplayName) is { Length: > 0 } slug ? slug : draft.Request.Id,
                            draft.Request.Description, draft.PrivateRepository, line => Avalonia.Threading.Dispatcher.UIThread.Post(() => SetStatus(line, StatusKind.Info, target.DisplayName)));
                        SetStatus(L.Tr("authoring.status.repoCreated", draft.Request.DisplayName, url), StatusKind.Success, target.DisplayName, key: key);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
                    {
                        DiagnosticLog.Write($"Putting {draft.Request.Id} on GitHub", ex);
                        SetStatus(L.Tr("authoring.status.repoFailed", draft.Request.DisplayName, ex.Message), StatusKind.Error, target.DisplayName, key: key);
                    }
                }
                else SetStatus(L.Tr("authoring.status.created", draft.Request.DisplayName, where), StatusKind.Success, target.DisplayName, key: key);
                if (ReferenceEquals(target, _install)) await ReloadInstalledAsync();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"Creating the package {draft.Request.Id}", ex);
                SetStatus(L.Tr("authoring.status.createFailed", ex.Message), StatusKind.Error, target.DisplayName, Retry, key);
            }
            finally { IsBusy = false; }
        }, alwaysQueue: true);

    private async Task PublishPackageAsync(BasisInstall target, string id, string displayName)
    {
        if (!_gitService.IsAvailable) { SetStatus(L.Tr("develop.status.gitRequired"), StatusKind.Error); return; }
        if (WorkingCloneMount(target, id) is not { } mount) { SetStatus(L.Tr("packages.status.mountFolderMissing", displayName), StatusKind.Error); return; }
        PublishPlan? plan;
        PublishDraft? draft;
        try
        {
            var subPath = BasisDevStore.Read(target.UnityProjectPath, id)?.Upstream.Path ?? UpmGitUrl.Parse(mount.OriginalManifestValue)?.Path;
            plan = await Publisher.PlanAsync(mount.FolderPath, subPath);
            if (plan is null) { SetStatus(L.Tr("authoring.status.notAPackage", displayName), StatusKind.Error, target.DisplayName); return; }
            var tags = await _gitService.ListTagsAsync(mount.FolderPath);
            var inRegistry = _catalog.Packages.ContainsKey(id) && !_unofficialIds.Contains(id);
            var model = new PublishPackageViewModel(new PublishHost(_ghAuth, _ghApi, PrePublishCheck), plan, tags, inRegistry, RegistryCategories());
            draft = await Dialogs.PublishPackageAsync(model);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Opening the publish dialog for {id}", ex);
            SetStatus(L.Tr("authoring.status.publishFailed", ex.Message), StatusKind.Error, target.DisplayName);
            return;
        }
        if (draft is not null) await RunPublishAsync(target, id, plan, draft);
    }

    private Task RunPublishAsync(BasisInstall target, string id, PublishPlan plan, PublishDraft draft) =>
        PackageWorkAsync(target, id, L.Tr("authoring.status.publishing", plan.DisplayName, draft.Request.Version), async () =>
        {
            var key = QueueKey(target, id);
            Task Retry() => PublishPackageAsync(target, id, plan.DisplayName);
            void Progress(string line) => Avalonia.Threading.Dispatcher.UIThread.Post(() => SetStatus(line, StatusKind.Info, target.DisplayName));
            IsBusy = true;
            SetStatus(L.Tr("authoring.status.publishing", plan.DisplayName, draft.Request.Version), StatusKind.Info, target.DisplayName);
            try
            {
                var current = plan;
                if (draft.CreateRepository)
                {
                    var package = PackageJsonFile.Read(plan.PackageRoot);
                    await Publisher.CreateRepositoryAsync(target, id, plan.Folder, draft.Token, draft.RepositoryName, package?.Description, draft.PrivateRepository, Progress);
                    var subPath = Path.GetRelativePath(plan.Folder, plan.PackageRoot).Replace('\\', '/');
                    current = await Publisher.PlanAsync(plan.Folder, subPath == "." ? null : subPath) ?? plan;
                }
                var result = await Publisher.PublishAsync(current, draft.Request, draft.Token, draft.User, Progress);
                if (!result.Ok)
                {
                    SetStatus(L.Tr("authoring.status.publishFailed", result.Message), StatusKind.Error, target.DisplayName, Retry, key);
                    return;
                }
                SetStatus(result.ReleaseFailed
                    ? L.Tr("authoring.status.publishedNoRelease", plan.DisplayName, draft.Request.Version, result.Message)
                    : L.Tr("authoring.status.published", plan.DisplayName, draft.Request.Version), result.ReleaseFailed ? StatusKind.Info : StatusKind.Success, target.DisplayName, key: key);
                if (draft.SubmitToRegistry) await SubmitToRegistryAsync(target, current, draft, Progress);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"Publishing {id}", ex);
                SetStatus(L.Tr("authoring.status.publishFailed", ex.Message), StatusKind.Error, target.DisplayName, Retry, key);
            }
            finally
            {
                IsBusy = false;
                if (ReferenceEquals(target, _install)) await ReloadInstalledAsync();
            }
        }, alwaysQueue: true);

    private async Task SubmitToRegistryAsync(BasisInstall target, PublishPlan plan, PublishDraft draft, Action<string> progress)
    {
        if (PackageJsonFile.Read(plan.PackageRoot) is not { } package || plan.Origin is not { IsGitHub: true } origin)
        {
            SetStatus(L.Tr("authoring.status.submitFailed", plan.DisplayName, L.Tr("authoring.status.noRepository")), StatusKind.Error, target.DisplayName);
            return;
        }
        SetStatus(L.Tr("authoring.status.submitting", plan.DisplayName), StatusKind.Info, target.DisplayName);
        var subPath = Path.GetRelativePath(plan.Folder, plan.PackageRoot).Replace('\\', '/');
        var server = Directory.Exists(Path.Combine(plan.PackageRoot, "Server~"));
        var entry = PackagePublishService.EntryFor(package, origin, subPath == "." ? null : subPath, draft.Category, draft.Tags, server);
        var result = await Publisher.SubmitToRegistryAsync(entry, draft.Token, draft.User, progress);
        if (!result.Ok)
        {
            SetStatus(L.Tr("authoring.status.submitFailed", plan.DisplayName, result.Message), StatusKind.Error, target.DisplayName,
                () => SubmitToRegistryAsync(target, plan, draft, progress), QueueKey(target, plan.PackageId) + "|registry");
            return;
        }
        SetStatus(L.Tr("authoring.status.submitted", plan.DisplayName, result.PullRequestUrl ?? ""), StatusKind.Success, target.DisplayName);
        if (!string.IsNullOrEmpty(result.PullRequestUrl)) ExternalLink.Open(result.PullRequestUrl);
    }

    private async Task CheckPublishAsync(PackageRow row)
    {
        if (!row.IsMounted || row.IsEmbedded || !MountService.IsWorkingClone(row.MountFolder) || !_gitService.IsAvailable) return;
        try
        {
            var access = await Publisher.CheckAccessAsync(row.MountFolder!, await _ghAuth.GetTokenAsync());
            row.CanPublish = access is PublishAccess.CanPush or PublishAccess.NoRemote;
        }
        catch (Exception ex) { DiagnosticLog.Write($"Checking whether {row.Name} can be published", ex); }
    }

    private async Task<string> DefaultAuthorAsync(BasisInstall target)
    {
        try
        {
            if (await _gitService.GetConfigValueAsync(target.RepoRoot, "user.name") is { Length: > 0 } name) return name.Trim();
        }
        catch (Exception ex) { DiagnosticLog.Write("Reading the git user name", ex); }
        return Environment.UserName;
    }

    private IReadOnlyList<string> RegistryCategories() =>
        DefaultRegistryCategories
            .Concat(_catalogService.AllLatest(_catalog).Select(v => v.Category ?? "").Where(c => c.Length > 0))
            .Where(c => !InternalRegistryCategories.Contains(c, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .Append("Misc")
            .ToList();

    private static string? FormatDate(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public sealed partial class PackageRow
{
    private bool _canPublish;
    public bool CanPublish { get => _canPublish; set => SetField(ref _canPublish, value); }
}
