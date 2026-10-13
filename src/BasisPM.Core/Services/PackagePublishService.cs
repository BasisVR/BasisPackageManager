using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public enum VersionBump { Patch, Minor, Major }

public enum PublishAccess { Unknown, NoRemote, NotGitHub, ReadOnly, CanPush }

public sealed record PublishPlan(string Folder, string PackageRoot, string PackageId, string DisplayName, string CurrentVersion, string SuggestedVersion,
    string? LastTag, string TagPrefix, IReadOnlyList<GitCommitInfo> Commits, string? Branch, UpmGitUrl? Origin, int ChangedFiles)
{
    public bool HasRemote => Origin is not null;
    public bool IsGitHub => Origin is { IsGitHub: true };
    public string TagFor(string version) => TagPrefix + version.Trim();
}

public sealed record PublishRequest(string Version, string Notes, bool CreateRelease = true);

public sealed record PublishResult(bool Ok, string Message, string? Tag = null, string? ReleaseUrl = null, bool ReleaseFailed = false)
{
    public static PublishResult Fail(string message) => new(false, message);
}

public sealed record RegistryEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
    public string? AuthorUrl { get; init; }
    public string Category { get; init; } = "Misc";
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public required string GitUrl { get; init; }
    public string? RepoUrl { get; init; }
    public string? Unity { get; init; }
    public string? BasisVersion { get; init; }
    public string? License { get; init; }
    public string Version { get; init; } = "0.0.0";
    public string Icon { get; init; } = "📦";
    public IReadOnlyDictionary<string, string>? Dependencies { get; init; }
    public bool Server { get; init; }
}

public sealed record RegistrySubmissionResult(bool Ok, string Message, string? PullRequestUrl = null);

public sealed class PackagePublishService
{
    public const string RegistryOwner = "BasisVR";
    public const string RegistryRepo = "BasisPackageManager";
    public const string RegistryFolder = "src/BasisPM.Server/seed/packages";

    private static readonly Regex UnityField = new(@"^\d{1,4}\.\d{1,2}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionProperty = new("\"version\"\\s*:\\s*\"(?<value>[^\"\\\\]*)\"", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly GitService _git;
    private readonly GitHubApiService _api;
    private readonly MountService _mounts;
    private readonly Func<string, string, string, string> _pushUrl;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public PackagePublishService(GitService git, GitHubApiService api, MountService mounts, Func<string, string, string, string>? pushUrl = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _git = git;
        _api = api;
        _mounts = mounts;
        _pushUrl = pushUrl ?? ((owner, repo, token) => $"https://x-access-token:{token}@github.com/{owner}/{repo}.git");
        _delay = delay ?? Task.Delay;
    }

    public static string? NextVersion(string current, VersionBump bump)
    {
        if (!SemVer.TryParse(current, out var version)) return null;
        return bump switch
        {
            VersionBump.Major => $"{version.Major + 1}.0.0",
            VersionBump.Minor => $"{version.Major}.{version.Minor + 1}.0",
            _ => version.PreRelease is null ? $"{version.Major}.{version.Minor}.{version.Patch + 1}" : $"{version.Major}.{version.Minor}.{version.Patch}",
        };
    }

    public static bool IsValidVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var text = version.Trim();
        return char.IsDigit(text[0]) && SemVer.TryParse(text, out _) && text.Split('-', '+')[0].Count(c => c == '.') == 2;
    }

    public static bool IsNewer(string version, string than) =>
        SemVer.TryParse(version, out var a) && (!SemVer.TryParse(than, out var b) || a.CompareTo(b) > 0);

    public static string DetectTagPrefix(IEnumerable<string> tags)
    {
        int prefixed = 0, plain = 0;
        foreach (var tag in tags)
        {
            if (tag.Length > 1 && (tag[0] == 'v' || tag[0] == 'V') && char.IsDigit(tag[1]) && SemVer.TryParse(tag, out _)) prefixed++;
            else if (tag.Length > 0 && char.IsDigit(tag[0]) && SemVer.TryParse(tag, out _)) plain++;
        }
        return plain > prefixed ? "" : "v";
    }

    public static string SetVersion(string packageJson, string version)
    {
        using (var document = JsonDocument.Parse(packageJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("version", out var current) && current.ValueKind == JsonValueKind.String)
            {
                var old = current.GetString() ?? "";
                foreach (Match match in VersionProperty.Matches(packageJson))
                    if (match.Groups["value"].Value == old)
                        return packageJson[..match.Groups["value"].Index] + version + packageJson[(match.Groups["value"].Index + match.Groups["value"].Length)..];
            }
        }
        var node = JsonNode.Parse(packageJson, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject()
            ?? throw new InvalidDataException("package.json isn't a JSON object.");
        var rebuilt = new JsonObject();
        foreach (var (key, value) in node.ToList())
        {
            node.Remove(key);
            rebuilt[key] = value;
            if (key == "name") rebuilt["version"] = version;
        }
        if (!rebuilt.ContainsKey("version")) rebuilt["version"] = version;
        return rebuilt.ToJsonString(Json) + "\n";
    }

    public static string PrependChangelog(string? existing, string version, DateOnly date, string notes)
    {
        var section = new StringBuilder();
        section.Append("## [").Append(version).Append("] - ").Append(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        var body = (notes ?? "").Replace("\r\n", "\n").Trim();
        if (body.Length > 0) section.Append('\n').Append(body).Append('\n');
        var text = (existing ?? "").Replace("\r\n", "\n");
        if (text.Trim().Length == 0) return "# Changelog\n\n" + section;
        var next = text.IndexOf("\n## ", StringComparison.Ordinal);
        var at = text.StartsWith("## ", StringComparison.Ordinal) ? 0 : next >= 0 ? next + 1 : -1;
        return at >= 0 ? text[..at] + section + "\n" + text[at..] : text.TrimEnd('\n') + "\n\n" + section;
    }

    public static string SuggestNotes(IEnumerable<GitCommitInfo> commits)
    {
        var lines = commits
            .Select(c => c.Subject.Trim())
            .Where(s => s.Length > 0 && !s.StartsWith("Release ", StringComparison.Ordinal) && !s.StartsWith("Merge ", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(s => "- " + s);
        return string.Join('\n', lines);
    }

    public static IReadOnlyList<PackageIssue> BasicChecks(string packageRoot, string? expectedId = null)
    {
        var issues = new List<PackageIssue>();
        var path = PackageJsonFile.PathIn(packageRoot);
        var id = expectedId ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(packageRoot));
        if (!File.Exists(path))
        {
            issues.Add(new PackageIssue(IssueCodes.ManifestMissing, IssueSeverity.Error, id, "package.json is missing.") { File = "package.json" });
            return issues;
        }
        var package = PackageJsonFile.Read(packageRoot);
        if (package is null)
        {
            issues.Add(new PackageIssue(IssueCodes.ManifestInvalid, IssueSeverity.Error, id, "package.json isn't valid JSON.") { File = "package.json" });
            return issues;
        }
        if (PackageScaffoldService.ValidateId(package.Name) is var problem && problem != PackageIdProblem.None)
            issues.Add(new PackageIssue(IssueCodes.NameInvalid, IssueSeverity.Error, id, PackageScaffoldService.DescribeIdProblem(problem, package.Name)) { File = "package.json" });
        else if (expectedId is not null && !string.Equals(package.Name, expectedId, StringComparison.OrdinalIgnoreCase))
            issues.Add(new PackageIssue(IssueCodes.NameMismatch, IssueSeverity.Error, id, $"package.json is named {package.Name}, but the project installs it as {expectedId}.") { File = "package.json" });
        if (!IsValidVersion(package.Version))
            issues.Add(new PackageIssue(IssueCodes.VersionInvalid, IssueSeverity.Error, id, $"\"{package.Version}\" isn't a version like 1.2.3.") { File = "package.json" });
        if (!string.IsNullOrWhiteSpace(package.Unity) && !UnityField.IsMatch(package.Unity.Trim()))
            issues.Add(new PackageIssue(IssueCodes.UnityInvalid, IssueSeverity.Error, id, $"\"unity\" should be a Unity version like 6000.0, not \"{package.Unity}\".") { File = "package.json" });
        if (string.IsNullOrWhiteSpace(package.DisplayName))
            issues.Add(new PackageIssue(IssueCodes.DisplayNameMissing, IssueSeverity.Warning, id, "package.json has no displayName, so Unity shows the id instead.") { File = "package.json" });
        if (string.IsNullOrWhiteSpace(package.Description))
            issues.Add(new PackageIssue(IssueCodes.DescriptionMissing, IssueSeverity.Warning, id, "package.json has no description.") { File = "package.json" });
        if (string.IsNullOrWhiteSpace(package.License) && !new[] { "LICENSE", "LICENSE.md", "LICENSE.txt" }.Any(f => File.Exists(Path.Combine(packageRoot, f))))
            issues.Add(new PackageIssue(IssueCodes.LicenseMissing, IssueSeverity.Warning, id, "The package has no license, so others can't tell whether they may use it."));
        if (!new[] { "README.md", "README", "README.txt" }.Any(f => File.Exists(Path.Combine(packageRoot, f))))
            issues.Add(new PackageIssue(IssueCodes.ReadmeMissing, IssueSeverity.Warning, id, "The package has no README.md."));
        return issues;
    }

    public static Task<IReadOnlyList<PackageIssue>> BasicChecksAsync(string packageRoot, CancellationToken ct = default) =>
        Task.Run(() => BasicChecks(packageRoot), ct);

    public static async Task<IReadOnlyList<PackageIssue>> FullChecksAsync(string? unityProjectPath, string packageRoot, CancellationToken ct = default)
    {
        var issues = (await BasicChecksAsync(packageRoot, ct).ConfigureAwait(false)).ToList();
        if (string.IsNullOrWhiteSpace(unityProjectPath)) return issues;
        try
        {
            var editor = UnityEditorLocator.FindInstalled(UnityProjectService.ReadUnityVersion(unityProjectPath)).FirstOrDefault()?.Path is { } exe ? Path.GetDirectoryName(exe) : null;
            foreach (var issue in await new PackageAnalyzer().CheckPackageAsync(unityProjectPath, packageRoot, null, editor, ct).ConfigureAwait(false))
                if (!issues.Any(i => i.Code == issue.Code && string.Equals(i.File, issue.File, StringComparison.OrdinalIgnoreCase))) issues.Add(issue);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { DiagnosticLog.Write($"Checking {packageRoot} before publishing", ex); }
        return issues;
    }

    public async Task<PublishPlan?> PlanAsync(string folder, string? subPath, CancellationToken ct = default)
    {
        if (!MountService.IsWorkingClone(folder)) return null;
        var root = BasisDevStore.PackageRoot(folder, subPath) ?? (File.Exists(PackageJsonFile.PathIn(folder)) ? folder : null);
        if (root is null) return null;
        var package = PackageJsonFile.Read(root);
        var id = string.IsNullOrWhiteSpace(package?.Name) ? Path.GetFileName(folder) : package!.Name;
        var current = package?.Version ?? "";
        var tags = await _git.ListTagsAsync(folder, ct).ConfigureAwait(false);
        var prefix = DetectTagPrefix(tags);
        var tagged = tags.Contains(prefix + current, StringComparer.Ordinal) || tags.Contains(current, StringComparer.Ordinal) || tags.Contains("v" + current, StringComparer.Ordinal);
        var lastTag = tags.Count == 0 ? null : await _git.GetLastTagAsync(folder, ct).ConfigureAwait(false);
        var commits = await _git.GetLogAsync(folder, lastTag is null ? "HEAD" : $"{lastTag}..HEAD", 100, ct).ConfigureAwait(false);
        var suggested = !IsValidVersion(current) ? PackageScaffoldService.InitialVersion : tagged ? NextVersion(current, VersionBump.Patch) ?? current : current;
        var branch = await _git.GetCurrentBranchAsync(folder, ct).ConfigureAwait(false);
        var origin = UpmGitUrl.Parse(await _git.GetRemoteUrlAsync(folder, "origin", ct).ConfigureAwait(false));
        var status = await _git.GetStatusAsync(folder, ct).ConfigureAwait(false);
        var name = string.IsNullOrWhiteSpace(package?.DisplayName) ? id : package!.DisplayName;
        return new PublishPlan(folder, root, id, name, current, suggested, lastTag, prefix, commits, branch, origin, status.ChangeCount);
    }

    public async Task<PublishAccess> CheckAccessAsync(string folder, string? token, CancellationToken ct = default)
    {
        if (!MountService.IsWorkingClone(folder)) return PublishAccess.Unknown;
        var remote = await _git.GetRemoteUrlAsync(folder, "origin", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(remote)) return PublishAccess.NoRemote;
        if (UpmGitUrl.Parse(remote) is not { IsGitHub: true } origin) return PublishAccess.NotGitHub;
        if (string.IsNullOrEmpty(token)) return PublishAccess.Unknown;
        try
        {
            var repo = await _api.GetRepoAsync(token, origin.Owner, origin.Repo, ct).ConfigureAwait(false);
            return repo is null ? PublishAccess.Unknown : repo.CanPush ? PublishAccess.CanPush : PublishAccess.ReadOnly;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            DiagnosticLog.Write($"Checking whether {origin.Slug} accepts pushes", ex);
            return PublishAccess.Unknown;
        }
    }

    public async Task<PublishResult> PublishAsync(PublishPlan plan, PublishRequest request, string token, GitHubUser user, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var version = request.Version.Trim();
        if (!IsValidVersion(version)) return PublishResult.Fail($"\"{version}\" isn't a version like 1.2.3.");
        if (plan.Branch is null) return PublishResult.Fail($"{plan.PackageId} is on a detached commit. Check out a branch in its folder, then publish again.");
        if (plan.Origin is not { IsGitHub: true } origin) return PublishResult.Fail($"{plan.PackageId} has no GitHub repository to publish to.");
        var tag = plan.TagFor(version);
        if ((await _git.ListTagsAsync(plan.Folder, ct).ConfigureAwait(false)).Contains(tag, StringComparer.Ordinal))
            return PublishResult.Fail($"The tag {tag} already exists. Pick a newer version.");
        if (await _git.GetOperationInProgressAsync(plan.Folder, ct).ConfigureAwait(false) is { } operation)
            return PublishResult.Fail($"A git {operation} is in progress in {plan.PackageId}. Finish or abort it first.");

        var packagePath = PackageJsonFile.PathIn(plan.PackageRoot);
        var changelogPath = Path.Combine(plan.PackageRoot, "CHANGELOG.md");
        var originalPackage = await File.ReadAllTextAsync(packagePath, ct).ConfigureAwait(false);
        var originalChangelog = File.Exists(changelogPath) ? await File.ReadAllTextAsync(changelogPath, ct).ConfigureAwait(false) : null;
        string updatedPackage;
        try { updatedPackage = SetVersion(originalPackage, version); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { return PublishResult.Fail($"package.json couldn't be read: {ex.Message}"); }

        var name = string.IsNullOrWhiteSpace(user.Name) ? user.Login : user.Name!;
        onProgress?.Invoke($"Writing version {version}…");
        await File.WriteAllTextAsync(packagePath, updatedPackage, new UTF8Encoding(false), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(changelogPath, PrependChangelog(originalChangelog, version, DateOnly.FromDateTime(DateTime.Now), request.Notes), new UTF8Encoding(false), ct).ConfigureAwait(false);

        var before = await _git.ResolveCommitAsync(plan.Folder, "HEAD", ct).ConfigureAwait(false);
        await _git.AddAllAsync(plan.Folder, ct).ConfigureAwait(false);
        var commit = await _git.CommitAsync(plan.Folder, $"Release {version}", name, user.NoReplyEmail, ct).ConfigureAwait(false);
        if (!commit.Ok)
        {
            await RestoreAsync(plan, packagePath, originalPackage, changelogPath, originalChangelog, null, null, ct).ConfigureAwait(false);
            return PublishResult.Fail($"Couldn't commit the release: {commit.Output}");
        }
        var tagResult = await _git.TagAnnotatedAsync(plan.Folder, tag, $"Release {version}", name, user.NoReplyEmail, ct).ConfigureAwait(false);
        if (!tagResult.Ok)
        {
            await RestoreAsync(plan, packagePath, originalPackage, changelogPath, originalChangelog, before, null, ct).ConfigureAwait(false);
            return PublishResult.Fail($"Couldn't create the tag {tag}: {tagResult.Output}");
        }

        onProgress?.Invoke($"Pushing {plan.Branch} and {tag}…");
        var push = await _git.PushRefsAsync(plan.Folder, _pushUrl(origin.Owner, origin.Repo, token), new[] { $"HEAD:refs/heads/{plan.Branch}", $"refs/tags/{tag}" }, atomic: true, onProgress, ct).ConfigureAwait(false);
        if (!push.Ok)
        {
            await RestoreAsync(plan, packagePath, originalPackage, changelogPath, originalChangelog, before, tag, ct).ConfigureAwait(false);
            return PublishResult.Fail($"Push failed, so nothing was published: {Scrub(push.Output, token)}");
        }
        if (await _git.ResolveCommitAsync(plan.Folder, "HEAD", ct).ConfigureAwait(false) is { } head)
            await _git.UpdateRefAsync(plan.Folder, $"refs/remotes/origin/{plan.Branch}", head, null, "publish", ct).ConfigureAwait(false);

        if (!request.CreateRelease) return new PublishResult(true, $"Published {plan.PackageId} {version} as {tag}.", tag);
        onProgress?.Invoke("Creating the GitHub release…");
        try
        {
            var release = await _api.CreateReleaseAsync(token, origin.Owner, origin.Repo, tag, $"{plan.DisplayName} {version}", request.Notes.Trim(), SemVer.TryParse(version, out var parsed) && parsed.PreRelease is not null, ct).ConfigureAwait(false);
            return new PublishResult(true, $"Published {plan.PackageId} {version} as {tag}.", tag, release.HtmlUrl);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException)
        {
            DiagnosticLog.Write($"Creating the GitHub release {tag} of {origin.Slug}", ex);
            return new PublishResult(true, $"Pushed {tag}, but GitHub couldn't create the release: {Scrub(ex.Message, token)}", tag, null, ReleaseFailed: true);
        }
    }

    private async Task RestoreAsync(PublishPlan plan, string packagePath, string originalPackage, string changelogPath, string? originalChangelog, string? before, string? tag, CancellationToken ct)
    {
        try
        {
            if (tag is not null) await _git.DeleteTagAsync(plan.Folder, tag, CancellationToken.None).ConfigureAwait(false);
            if (before is not null && await _git.ResolveCommitAsync(plan.Folder, "HEAD", CancellationToken.None).ConfigureAwait(false) != before)
                await _git.ResetSoftAsync(plan.Folder, before, CancellationToken.None).ConfigureAwait(false);
            await _git.UnstageAllAsync(plan.Folder, CancellationToken.None).ConfigureAwait(false);
            await File.WriteAllTextAsync(packagePath, originalPackage, new UTF8Encoding(false), CancellationToken.None).ConfigureAwait(false);
            if (originalChangelog is null) File.Delete(changelogPath);
            else await File.WriteAllTextAsync(changelogPath, originalChangelog, new UTF8Encoding(false), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagnosticLog.Write($"Undoing the release of {plan.PackageId}", ex);
        }
    }

    public async Task<string> CreateRepositoryAsync(BasisInstall install, string packageId, string folder, string token, string repoName, string? description, bool isPrivate, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (!MountService.IsWorkingClone(folder)) throw new InvalidOperationException($"{folder} isn't a git repository.");
        if (!string.IsNullOrWhiteSpace(await _git.GetRemoteUrlAsync(folder, "origin", ct).ConfigureAwait(false)))
            throw new InvalidOperationException($"{packageId} already has a remote repository.");
        onProgress?.Invoke($"Creating the GitHub repository {repoName}…");
        var repo = await _api.CreateRepoAsync(token, repoName, description, isPrivate, ct).ConfigureAwait(false);
        var owner = repo.Owner?.Login ?? repo.FullName.Split('/')[0];
        var cloneUrl = repo.CloneUrl.Length > 0 ? repo.CloneUrl : $"https://github.com/{repo.FullName}.git";
        var remote = await _git.SetRemoteAsync(folder, "origin", cloneUrl, ct).ConfigureAwait(false);
        if (!remote.Ok) throw new InvalidOperationException($"Created {repo.FullName}, but couldn't add it as the remote: {remote.Output}");
        var branch = await _git.GetCurrentBranchAsync(folder, ct).ConfigureAwait(false) ?? "main";
        onProgress?.Invoke($"Pushing {branch}…");
        var push = await _git.PushRefsAsync(folder, _pushUrl(owner, repo.Name, token), new[] { $"HEAD:refs/heads/{branch}" }, atomic: false, onProgress, ct).ConfigureAwait(false);
        if (!push.Ok) throw new InvalidOperationException($"Created {repo.FullName}, but the push failed: {Scrub(push.Output, token)}");
        if (await _git.ResolveCommitAsync(folder, "HEAD", ct).ConfigureAwait(false) is { } head)
            await _git.UpdateRefAsync(folder, $"refs/remotes/origin/{branch}", head, null, "publish", ct).ConfigureAwait(false);
        await _git.SetConfigAsync(folder, $"branch.{branch}.remote", "origin", ct).ConfigureAwait(false);
        await _git.SetConfigAsync(folder, $"branch.{branch}.merge", $"refs/heads/{branch}", ct).ConfigureAwait(false);
        var line = UnityPackageMap.ReadDependencies(Path.Combine(install.UnityProjectPath, "Packages", "manifest.json")).GetValueOrDefault(packageId);
        await _mounts.RecordAsync(install, packageId, folder, cloneUrl, line).ConfigureAwait(false);
        return repo.HtmlUrl.Length > 0 ? repo.HtmlUrl : $"https://github.com/{repo.FullName}";
    }

    public static string RegistryJson(RegistryEntry entry)
    {
        var json = new JsonObject
        {
            ["id"] = entry.Id,
            ["name"] = entry.Name.Trim(),
            ["description"] = entry.Description.Trim(),
            ["author"] = entry.Author.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(entry.AuthorUrl)) json["authorUrl"] = entry.AuthorUrl.Trim();
        json["category"] = string.IsNullOrWhiteSpace(entry.Category) ? "Misc" : entry.Category.Trim();
        json["tags"] = new JsonArray(entry.Tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).Select(t => (JsonNode)JsonValue.Create(t)!).ToArray());
        json["source"] = "community";
        json["gitUrl"] = entry.GitUrl;
        if (!string.IsNullOrWhiteSpace(entry.RepoUrl)) json["repoUrl"] = entry.RepoUrl;
        if (!string.IsNullOrWhiteSpace(entry.Unity)) json["unity"] = entry.Unity;
        if (!string.IsNullOrWhiteSpace(entry.BasisVersion)) json["basisVersion"] = entry.BasisVersion;
        if (!string.IsNullOrWhiteSpace(entry.License)) json["license"] = entry.License;
        json["version"] = entry.Version;
        json["icon"] = string.IsNullOrWhiteSpace(entry.Icon) ? "📦" : entry.Icon;
        if (entry.Dependencies is { Count: > 0 } dependencies)
        {
            var map = new JsonObject();
            foreach (var (id, range) in dependencies.OrderBy(d => d.Key, StringComparer.Ordinal)) map[id] = range;
            json["dependencies"] = map;
        }
        if (entry.Server) json["server"] = true;
        return json.ToJsonString(Json) + "\n";
    }

    public static RegistryEntry EntryFor(UpmPackageJson package, UpmGitUrl origin, string? subPath, string category, IEnumerable<string> tags, bool server)
    {
        var gitUrl = origin.ToManifestUrl(null, string.IsNullOrWhiteSpace(subPath) ? null : subPath);
        return new RegistryEntry
        {
            Id = package.Name,
            Name = string.IsNullOrWhiteSpace(package.DisplayName) ? package.Name : package.DisplayName,
            Description = package.Description,
            Author = string.IsNullOrWhiteSpace(package.Author?.Name) ? origin.Owner : package.Author!.Name,
            AuthorUrl = string.IsNullOrWhiteSpace(package.Author?.Url) ? $"https://github.com/{origin.Owner}" : package.Author!.Url,
            Category = category,
            Tags = tags.ToList(),
            GitUrl = gitUrl,
            RepoUrl = $"https://github.com/{origin.Owner}/{origin.Repo}",
            Unity = package.Unity,
            BasisVersion = package.Basis?.From,
            License = package.License,
            Version = package.Version,
            Server = server,
        };
    }

    public async Task<RegistrySubmissionResult> SubmitToRegistryAsync(RegistryEntry entry, string token, GitHubUser user, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var problem = PackageScaffoldService.ValidateId(entry.Id);
        if (problem != PackageIdProblem.None) return new RegistrySubmissionResult(false, PackageScaffoldService.DescribeIdProblem(problem, entry.Id));
        if (!GitUrlPolicy.IsHostedGitUrl(UpmGitUrl.Parse(entry.GitUrl)?.CloneUrl))
            return new RegistrySubmissionResult(false, "The registry only lists packages hosted on GitHub or GitLab.");
        try
        {
            var upstream = await _api.GetRepoAsync(token, RegistryOwner, RegistryRepo, ct).ConfigureAwait(false);
            if (upstream is null) return new RegistrySubmissionResult(false, $"Couldn't look up {RegistryOwner}/{RegistryRepo} on GitHub. Check your connection and sign-in, then try again.");
            var baseBranch = upstream.DefaultBranch is { Length: > 0 } branchName ? branchName : "main";
            string owner = RegistryOwner, repo = RegistryRepo;
            if (!upstream.CanPush)
            {
                onProgress?.Invoke($"Forking {RegistryOwner}/{RegistryRepo}…");
                var fork = await _api.ForkRepoAsync(token, RegistryOwner, RegistryRepo, ct).ConfigureAwait(false);
                owner = fork.Owner?.Login ?? user.Login;
                repo = fork.Name;
                for (var attempt = 0; attempt < 10 && await _api.GetRepoAsync(token, owner, repo, ct).ConfigureAwait(false) is null; attempt++)
                {
                    onProgress?.Invoke("Waiting for the fork to be ready…");
                    await _delay(TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
                }
                await _api.SyncForkAsync(token, owner, repo, baseBranch, ct).ConfigureAwait(false);
            }

            var baseSha = await _api.GetBranchShaAsync(token, owner, repo, baseBranch, ct).ConfigureAwait(false);
            if (baseSha is null) return new RegistrySubmissionResult(false, $"Couldn't read the {baseBranch} branch of {owner}/{repo}.");
            var branch = "add-package-" + PackageScaffoldService.Slug(entry.Id);
            if (!await _api.CreateBranchAsync(token, owner, repo, branch, baseSha, ct).ConfigureAwait(false))
            {
                branch += "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                if (!await _api.CreateBranchAsync(token, owner, repo, branch, baseSha, ct).ConfigureAwait(false))
                    return new RegistrySubmissionResult(false, $"Couldn't create a branch on {owner}/{repo}.");
            }

            var path = $"{RegistryFolder}/{entry.Id}.json";
            var existing = await _api.GetFileShaAsync(token, owner, repo, path, branch, ct).ConfigureAwait(false);
            onProgress?.Invoke($"Adding {path}…");
            await _api.PutFileAsync(token, owner, repo, path, branch, $"{(existing is null ? "Add" : "Update")} {entry.Id} in the registry", RegistryJson(entry), existing, ct).ConfigureAwait(false);

            var head = owner == RegistryOwner ? branch : $"{owner}:{branch}";
            onProgress?.Invoke("Opening the pull request…");
            if (await _api.FindOpenPullRequestAsync(token, RegistryOwner, RegistryRepo, head, ct).ConfigureAwait(false) is { } open)
                return new RegistrySubmissionResult(true, $"Updated the registry pull request for {entry.Id}.", open.HtmlUrl);
            var body = $"Adds [{entry.Name}]({entry.RepoUrl ?? entry.GitUrl}) ({entry.Id} {entry.Version}) to the Basis package registry.\n\n{entry.Description}".Trim();
            var pull = await _api.CreatePullRequestAsync(token, RegistryOwner, RegistryRepo, $"Add package: {entry.Name}", head, baseBranch, body, ct).ConfigureAwait(false);
            return new RegistrySubmissionResult(true, $"Opened a registry pull request for {entry.Id}.", pull.HtmlUrl);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException)
        {
            DiagnosticLog.Write($"Submitting {entry.Id} to the registry", ex);
            return new RegistrySubmissionResult(false, Scrub(ex.Message, token));
        }
    }

    private static string Scrub(string text, string token) => string.IsNullOrEmpty(token) ? text : text.Replace(token, "***");
}
