using System.Globalization;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    private PackageScaffoldService? _scaffold;
    private PackagePublishService? _publisher;

    private PackageScaffoldService Scaffold => _scaffold ??= new PackageScaffoldService(_git, _projects, _mounts);
    private PackagePublishService Publisher => _publisher ??= new PackagePublishService(_git, _ghApi, _mounts);

    private async Task NewPackageAsync(List<string> values)
    {
        var options = values.ToList();
        var name = TakeValue(options, "--name");
        var description = TakeValue(options, "--description") ?? "";
        var author = TakeValue(options, "--author");
        var licenseText = TakeValue(options, "--license");
        var references = TakeValues(options, "--reference");
        var noRuntime = TakeFlag(options, "--no-runtime");
        var noEditor = TakeFlag(options, "--no-editor");
        var server = TakeFlag(options, "--server");
        var samples = TakeFlag(options, "--samples");
        var github = TakeFlag(options, "--github");
        var isPrivate = TakeFlag(options, "--private");
        Expect(options, 1, 1, "a package id, such as com.yourname.yourpackage");
        var id = options[0].Trim();
        var problem = PackageScaffoldService.ValidateId(id);
        if (problem != PackageIdProblem.None) throw new UsageException(PackageScaffoldService.DescribeIdProblem(problem, id));
        var license = (licenseText ?? "mit").Trim().ToLowerInvariant() switch
        {
            "mit" => ScaffoldLicense.Mit,
            "apache" or "apache-2.0" or "apache2" => ScaffoldLicense.Apache2,
            "none" => ScaffoldLicense.None,
            _ => throw new UsageException($"--license takes mit, apache-2.0 or none, not '{licenseText}'."),
        };
        var install = await LoadInstallAsync();
        if (!_git.IsAvailable) throw new InvalidOperationException("Git was not found. Install Git and make sure it is on your PATH.");
        if (Scaffold.FindConflict(install, id) is { } conflict) throw new InvalidOperationException(conflict);

        string? token = null;
        if (github)
            token = await TryGitHubTokenAsync() ?? throw new InvalidOperationException("Sign in to GitHub first: run 'gh auth login', or set GH_TOKEN to a token that can create repositories.");

        var assemblies = PackageScaffoldService.FindBasisAssemblies(install.UnityProjectPath);
        if (references.Count > 0)
        {
            var unknown = references.Where(r => !assemblies.Any(a => a.Name == r)).ToList();
            if (unknown.Count > 0) Out.Warning($"Not found among this project's Basis assemblies: {string.Join(", ", unknown)}. They are referenced anyway.");
        }
        else references = PackageScaffoldService.DefaultReferences.Where(r => assemblies.Any(a => a.Name == r)).ToList();
        DateOnly? basis = null;
        await RunOperationAsync(async ct => basis = await Scaffold.ResolveBasisDateAsync(_updates, install, ct), "Working out the Basis version" + Out.Ellipsis);
        var request = new PackageScaffoldRequest
        {
            Id = id,
            DisplayName = name ?? TitleFromId(id),
            Description = description,
            Author = author ?? await _git.GetConfigValueAsync(install.RepoRoot, "user.name") ?? Environment.UserName,
            License = license,
            Unity = PackageScaffoldService.UnityMajorMinor(install.UnityVersion),
            BasisFrom = basis?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            References = references,
            Runtime = !noRuntime,
            Editor = !noEditor,
            Server = server,
            Samples = samples,
        };
        PackageScaffoldResult result = null!;
        await RunOperationAsync(async ct => result = await Scaffold.CreateAsync(install, request, ct), $"Creating {id}" + Out.Ellipsis);
        if (!result.Ok) throw new InvalidOperationException(result.Message);
        Out.Success(result.Message);
        if (github && token is not null && result.Folder is { } folder)
        {
            string url = null!;
            await RunOperationAsync(async ct => url = await Publisher.CreateRepositoryAsync(install, id, folder, token, PackageScaffoldService.Slug(request.DisplayName) is { Length: > 0 } slug ? slug : id,
                request.Description, isPrivate, Out.Progress, ct), "Putting it on GitHub" + Out.Ellipsis);
            Out.Success($"Pushed it to {url}.");
        }
        Out.Hint($"Edit it in Unity, then release it with '{Prefix}publish-package {id}'.");
    }

    private static string TitleFromId(string id)
    {
        var last = id.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? id;
        return string.Join(' ', last.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private async Task PublishPackageAsync(List<string> values)
    {
        var options = values.ToList();
        var version = TakeValue(options, "--version");
        var bumpText = TakeValue(options, "--bump");
        var notes = TakeValue(options, "--notes");
        var noRelease = TakeFlag(options, "--no-release");
        var submit = TakeFlag(options, "--submit");
        var category = TakeValue(options, "--category") ?? "Misc";
        var tags = TakeValues(options, "--tag");
        var dryRun = TakeFlag(options, "--dry-run");
        var assumeYes = TakeFlag(options, "--yes") | TakeFlag(options, "-y");
        Expect(options, 1, 1, "a package id");
        if (version is not null && bumpText is not null) throw new UsageException("Use --version or --bump, not both.");
        var install = await LoadInstallAsync();
        var id = options[0];
        if (WorkingCloneMount(install, id) is not { } mount)
            throw new InvalidOperationException($"{id} isn't a development clone in this project, so there's nothing to publish from. Create one with '{Prefix}new-package' or install it first.");
        var subPath = BasisDevStore.Read(install.UnityProjectPath, id)?.Upstream.Path ?? UpmGitUrl.Parse(mount.OriginalManifestValue)?.Path;
        PublishPlan? plan = null;
        IReadOnlyList<PackageIssue> issues = Array.Empty<PackageIssue>();
        await RunOperationAsync(async ct =>
        {
            plan = await Publisher.PlanAsync(mount.FolderPath, subPath, ct);
            if (plan is not null) issues = await PackagePublishService.FullChecksAsync(install.UnityProjectPath, plan.PackageRoot, ct);
        }, $"Reading {id}" + Out.Ellipsis);
        if (plan is null) throw new InvalidOperationException($"{id} has no package.json in its clone, so it can't be published.");

        var next = version?.Trim() ?? (bumpText?.Trim().ToLowerInvariant() switch
        {
            null => plan.SuggestedVersion,
            "patch" => PackagePublishService.NextVersion(plan.CurrentVersion, VersionBump.Patch),
            "minor" => PackagePublishService.NextVersion(plan.CurrentVersion, VersionBump.Minor),
            "major" => PackagePublishService.NextVersion(plan.CurrentVersion, VersionBump.Major),
            _ => throw new UsageException($"--bump takes patch, minor or major, not '{bumpText}'."),
        }) ?? plan.SuggestedVersion;
        var releaseNotes = notes ?? PackagePublishService.SuggestNotes(plan.Commits);

        if (Out.JsonMode)
        {
            Out.Json(new
            {
                package = plan.PackageId,
                current = plan.CurrentVersion,
                lastTag = plan.LastTag,
                version = next,
                tag = plan.TagFor(next),
                branch = plan.Branch,
                repository = plan.Origin?.Slug,
                changedFiles = plan.ChangedFiles,
                commits = plan.Commits.Select(c => new { sha = c.Sha, subject = c.Subject }),
                notes = releaseNotes,
                issues = issues.Select(i => new { code = i.Code, severity = i.Severity.ToString().ToLowerInvariant(), message = i.Message, file = i.File }),
            });
            if (dryRun || issues.Any(i => i.Severity == IssueSeverity.Error)) return;
        }
        else
        {
            Out.Say(new Span(plan.DisplayName, Tone.Bold), new Span($"  {plan.PackageId}", Tone.Dim));
            Out.Say(new Span("Version ", Tone.Dim), new Span(plan.CurrentVersion), new Span(plan.LastTag is null ? "  (not released yet)" : $"  (last release {plan.LastTag})", Tone.Dim));
            Out.Say(new Span("Publishes ", Tone.Dim), new Span(next, Tone.Good), new Span($" as tag {plan.TagFor(next)}" + (plan.Origin is { } origin ? $" to {origin.Slug}" : ""), Tone.Dim));
            if (plan.ChangedFiles > 0) Out.Warning($"{Plural(plan.ChangedFiles, "file")} with uncommitted changes will be committed as part of the release.");
            foreach (var issue in issues)
            {
                if (issue.Severity == IssueSeverity.Error) Out.Error(issue.Message);
                else Out.Warning(issue.Message);
            }
            if (releaseNotes.Length > 0)
            {
                Out.Heading("Release notes");
                foreach (var line in releaseNotes.Split('\n')) Out.Say("  " + line);
            }
        }
        if (issues.Any(i => i.Severity == IssueSeverity.Error)) throw new InvalidOperationException("Fix the problems above, then publish again.");
        if (!PackagePublishService.IsValidVersion(next)) throw new UsageException($"'{next}' isn't a version like 1.2.3.");
        if (dryRun)
        {
            Out.Hint("Nothing was changed (--dry-run).");
            return;
        }
        if (plan.Branch is null) throw new InvalidOperationException($"{id} is on a detached commit. Check out a branch in {RelativeTo(install.UnityProjectPath, plan.Folder)} first.");
        if (!plan.HasRemote) throw new InvalidOperationException($"{id} has no remote repository yet. Put it on GitHub with '{Prefix}new-package --github' for new packages, or add an 'origin' remote.");
        if (!plan.IsGitHub) throw new InvalidOperationException($"{id}'s repository isn't on GitHub, so it can't be published from here.");

        var token = await TryGitHubTokenAsync() ?? throw new InvalidOperationException("Sign in to GitHub first: run 'gh auth login', or set GH_TOKEN to a token that can push to the package's repository.");
        var user = await _ghApi.GetUserAsync(token) ?? throw new InvalidOperationException("GitHub didn't accept that token. Sign in again with 'gh auth login'.");
        if (!assumeYes && !Confirm($"Publish {plan.PackageId} {next} to {plan.Origin!.Slug} as {user.Login}?"))
        {
            Out.Warning("Cancelled.");
            return;
        }
        PublishResult result = null!;
        await RunOperationAsync(async ct => result = await Publisher.PublishAsync(plan, new PublishRequest(next, releaseNotes, !noRelease), token, user, Out.Progress, ct), "Publishing" + Out.Ellipsis);
        if (!result.Ok) throw new InvalidOperationException(result.Message);
        if (result.ReleaseFailed) Out.Warning(result.Message);
        else Out.Success(result.Message + (result.ReleaseUrl is null ? "" : $" Release: {result.ReleaseUrl}"));
        if (!submit) return;
        if (PackageJsonFile.Read(plan.PackageRoot) is not { } package) throw new InvalidOperationException("package.json couldn't be read after publishing.");
        var relative = Path.GetRelativePath(plan.Folder, plan.PackageRoot).Replace('\\', '/');
        var entry = PackagePublishService.EntryFor(package, plan.Origin!, relative == "." ? null : relative, category, tags, Directory.Exists(Path.Combine(plan.PackageRoot, "Server~")));
        RegistrySubmissionResult submitted = null!;
        await RunOperationAsync(async ct => submitted = await Publisher.SubmitToRegistryAsync(entry, token, user, Out.Progress, ct), "Opening the registry pull request" + Out.Ellipsis);
        if (!submitted.Ok) throw new InvalidOperationException(submitted.Message);
        Out.Success($"{submitted.Message} {submitted.PullRequestUrl}");
    }
}
