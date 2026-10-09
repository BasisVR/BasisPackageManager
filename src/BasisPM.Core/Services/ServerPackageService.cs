using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

/// <summary>
/// Manages the packages a project's Basis Server is built with. <c>Basis Server/Packages/manifest.json</c> lists them
/// (git URL with optional <c>?path=</c>/<c>#ref</c>, or <c>file:</c> path); <c>packages-lock.props</c> is generated from it
/// and tells the server build which package folders compile into which server assembly; <c>packages-local.props</c>
/// holds this machine's folder links. Package clones live in <c>Basis Server/Packages/&lt;id&gt;</c>.
/// </summary>
public sealed class ServerPackageService
{
    public const string ServerFolderName = "Basis Server";
    public const string PackagesFolderName = "Packages";
    public const string ManifestFileName = "manifest.json";
    public const string LockFileName = "packages-lock.props";
    public const string LocalFileName = "packages-local.props";
    public const string MetadataPrefix = "BasisServerPackage:";

    public static readonly IReadOnlyList<string> HostAssemblies = new[] { "BasisNetworkCore", "BasisNetworkServer", "BasisNetworkClient", "BasisNetworkConsole", "BasisServerTests" };

    private static readonly IReadOnlyDictionary<string, string> HostGuids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["4fbaa9af51b2f9c408a9dff4518adb81"] = "BasisNetworkCore",
        ["b7ce407150fbecd4c84e6256f87d3364"] = "BasisNetworkServer",
        ["e0cd46be3f661ad47be966c5ed026053"] = "BasisNetworkClient",
    };

    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex NuGetIdPattern = new(@"^[A-Za-z0-9_][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex NuGetVersionPattern = new(@"^[0-9A-Za-z.*+\-\[\]\(\), ]+$", RegexOptions.CultureInvariant);
    private static readonly string[] SkippedFolders = { "obj", "bin", "node_modules", "Library" };

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions PackageJson = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly GitService _git;

    public ServerPackageService(GitService git) => _git = git;

    public static string ServerRoot(string repoRoot) => Path.Combine(repoRoot, ServerFolderName);
    public static string PackagesDirectory(string repoRoot) => Path.Combine(ServerRoot(repoRoot), PackagesFolderName);
    public static string ManifestPath(string repoRoot) => Path.Combine(PackagesDirectory(repoRoot), ManifestFileName);
    public static string LockPath(string repoRoot) => Path.Combine(PackagesDirectory(repoRoot), LockFileName);
    public static string LocalPath(string repoRoot) => Path.Combine(PackagesDirectory(repoRoot), LocalFileName);

    public static bool HasServer(string repoRoot) =>
        !string.IsNullOrWhiteSpace(repoRoot) && File.Exists(Path.Combine(ServerRoot(repoRoot), "BasisNetworkCore", "BasisNetworkCore.csproj"));

    /// <summary>True when this checkout's server build stitches packages in (it imports <c>packages-lock.props</c>).</summary>
    public static bool SupportsPackages(string repoRoot)
    {
        var targets = Path.Combine(ServerRoot(repoRoot), "Directory.Build.targets");
        try { return HasServer(repoRoot) && File.Exists(targets) && File.ReadAllText(targets).Contains(LockFileName, StringComparison.Ordinal); }
        catch (IOException) { return false; }
    }

    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 214 && IdPattern.IsMatch(id) && !id.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// Turns what a user typed into an install source: <c>file:</c> paths and git URLs pass through, an existing folder
    /// becomes a <c>file:</c> path relative to the Packages folder, and a bare package id is looked up in the catalog.
    /// </summary>
    public static ServerPackageSource ResolveSource(string repoRoot, string value, Catalog? catalog)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) return new ServerPackageSource("", null, null, "Enter a package id, git URL or file: path.");
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return new ServerPackageSource(value, null, null, null);
        if (Path.IsPathRooted(value) && Directory.Exists(value) || value.StartsWith('.') && Directory.Exists(value))
            return new ServerPackageSource("file:" + Path.GetRelativePath(PackagesDirectory(repoRoot), Path.GetFullPath(value)).Replace('\\', '/'), null, null, null);
        if (!IsValidId(value) || value.Contains('/') || value.Contains(':')) return new ServerPackageSource(value, null, null, null);
        var entry = catalog?.Packages
            .Where(p => string.Equals(p.Key, value, StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Value.Versions.Values)
            .Where(v => !string.IsNullOrWhiteSpace(v.Url))
            .OrderByDescending(v => SemVer.TryParse(v.Version, out var parsed) ? parsed : new SemVer(-1, 0, 0))
            .FirstOrDefault();
        return entry is null
            ? new ServerPackageSource(value, null, null, $"Package '{value}' was not found in the package registry. Use its git URL instead.")
            : new ServerPackageSource(entry.Url!, entry.Name, entry, null);
    }

    public async Task<bool> HasLocalWorkAsync(string repoRoot, string id, CancellationToken ct = default)
    {
        if (!IsValidId(id) || LoadLinks(repoRoot).ContainsKey(id)) return false;
        var clone = Path.Combine(PackagesDirectory(repoRoot), id);
        return Directory.Exists(clone) && await HasLocalWorkAsync(clone, ct).ConfigureAwait(false);
    }

    public ServerPackageManifest LoadManifest(string repoRoot)
    {
        var path = ManifestPath(repoRoot);
        if (!File.Exists(path)) return new ServerPackageManifest();
        try
        {
            var manifest = JsonSerializer.Deserialize<ServerPackageManifest>(File.ReadAllText(path), ManifestJson) ?? new ServerPackageManifest();
            manifest.Dependencies ??= new();
            return manifest;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} isn't valid JSON, so it was left unchanged. Fix or restore it, then try again.", ex);
        }
    }

    public void SaveManifest(string repoRoot, ServerPackageManifest manifest)
    {
        manifest.Dependencies = manifest.Dependencies.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value);
        AtomicFile.WriteAllText(ManifestPath(repoRoot), JsonSerializer.Serialize(manifest, ManifestJson) + "\n");
    }

    public IReadOnlyDictionary<string, ServerPackageLockEntry> LoadLock(string repoRoot)
    {
        var result = new Dictionary<string, ServerPackageLockEntry>(StringComparer.Ordinal);
        var document = LoadXml(LockPath(repoRoot));
        if (document is null) return result;
        var modules = document.Descendants("BasisServerPackageModule")
            .GroupBy(e => Attr(e, "Include"), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ServerPackageModule>)g
                .Select(e => new ServerPackageModule(Unescape(Attr(e, "Path")), Attr(e, "Assembly"),
                    Unescape(Attr(e, "Excludes")).Split('|', StringSplitOptions.RemoveEmptyEntries)))
                .ToList(), StringComparer.Ordinal);
        var nuget = document.Descendants("BasisServerPackageNuGet")
            .GroupBy(e => Attr(e, "Include"), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, string>)g
                .GroupBy(e => Attr(e, "Package"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(p => p.Key, p => Unescape(Attr(p.Last(), "Version")), StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        foreach (var element in document.Descendants("BasisServerPackage"))
        {
            var id = Attr(element, "Include");
            if (!IsValidId(id)) continue;
            result[id] = new ServerPackageLockEntry(id, Unescape(Attr(element, "Version")), Unescape(Attr(element, "Source")),
                Blank(Unescape(Attr(element, "Url"))), Blank(Unescape(Attr(element, "Ref"))), Blank(Attr(element, "Commit")),
                Blank(Unescape(Attr(element, "SubPath"))), Blank(Unescape(Attr(element, "LocalPath"))),
                modules.GetValueOrDefault(id) ?? Array.Empty<ServerPackageModule>(),
                nuget.GetValueOrDefault(id) ?? new Dictionary<string, string>());
        }
        return result;
    }

    public IReadOnlyDictionary<string, string> LoadLinks(string repoRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var document = LoadXml(LocalPath(repoRoot));
        if (document is null) return result;
        foreach (var element in document.Descendants("BasisServerPackageLink"))
        {
            var id = Attr(element, "Include");
            var folder = Unescape(Attr(element, "Folder"));
            if (IsValidId(id) && folder.Length > 0) result[id] = folder;
        }
        return result;
    }

    public static ServerPackageDeclaration ReadDeclaration(string packageRoot)
    {
        var problems = new List<string>();
        var found = new List<(string Path, string Assembly)>();
        var nuget = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string id = "", version = "0.0.0", displayName = "", description = "";
        var packageJson = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(packageJson))
        {
            problems.Add($"package.json was not found in {packageRoot}.");
            return new ServerPackageDeclaration(id, version, displayName, description, Array.Empty<ServerPackageModule>(), nuget, problems);
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJson), PackageJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("The file does not contain a JSON object.");
            id = Text(root, "name") ?? "";
            version = Text(root, "version") ?? "0.0.0";
            displayName = Text(root, "displayName") ?? id;
            description = Text(root, "description") ?? "";
            if (!IsValidId(id)) problems.Add($"\"{id}\" is not a valid package name (lowercase letters, digits, '.', '-' and '_').");
            var explicitModules = false;
            if (root.TryGetProperty("basisServer", out var server) && server.ValueKind == JsonValueKind.Object)
            {
                if (server.TryGetProperty("modules", out var modules) && modules.ValueKind == JsonValueKind.Array)
                {
                    explicitModules = true;
                    foreach (var module in modules.EnumerateArray())
                    {
                        var path = (Text(module, "path") ?? "").Replace('\\', '/').Trim('/');
                        var requested = Text(module, "assembly") ?? "";
                        var assembly = HostAssemblies.FirstOrDefault(h => h.Equals(requested, StringComparison.OrdinalIgnoreCase));
                        if (assembly is null) problems.Add($"Module \"{path}\" targets \"{requested}\"; server packages compile into {string.Join(", ", HostAssemblies)}.");
                        else if (!GitUrlPolicy.IsSafeSubPath(path)) problems.Add($"Module \"{path}\" must stay inside the package.");
                        else if (!Directory.Exists(Path.Combine(packageRoot, path))) problems.Add($"Module folder \"{path}\" was not found.");
                        else found.Add((path, assembly));
                    }
                }
                if (server.TryGetProperty("nuget", out var packages) && packages.ValueKind == JsonValueKind.Object)
                {
                    foreach (var package in packages.EnumerateObject())
                    {
                        var packageVersion = package.Value.ValueKind == JsonValueKind.String ? package.Value.GetString() : null;
                        if (!NuGetIdPattern.IsMatch(package.Name) || packageVersion is null || !NuGetVersionPattern.IsMatch(packageVersion))
                            problems.Add($"The NuGet dependency \"{package.Name}\" needs a valid id and version.");
                        else nuget[package.Name] = packageVersion;
                    }
                }
            }
            if (!explicitModules) Discover(packageRoot, found);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            problems.Add($"package.json could not be read: {ex.Message}");
        }

        var distinct = found.Distinct().ToList();
        var result = distinct.Select(m => new ServerPackageModule(m.Path, m.Assembly, Excludes(packageRoot, m.Path, distinct))).ToList();
        if (problems.Count == 0 && result.Count == 0)
            problems.Add("The package has no server code: add a \"basisServer\" section to package.json, an .asmref into a Basis network assembly, or a Server~/<assembly> folder.");
        return new ServerPackageDeclaration(id, version, displayName, description, result, nuget, problems);
    }

    public async Task<IReadOnlyList<ServerPackageInfo>> ListAsync(string repoRoot, CancellationToken ct = default)
    {
        var manifest = LoadManifest(repoRoot);
        var locks = LoadLock(repoRoot);
        var links = LoadLinks(repoRoot);
        var result = new List<ServerPackageInfo>();
        foreach (var (id, source) in manifest.Dependencies.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            locks.TryGetValue(id, out var locked);
            links.TryGetValue(id, out var link);
            var place = Locate(repoRoot, id, source, link);
            var declaration = place.Root is not null && File.Exists(Path.Combine(place.Root, "package.json")) ? ReadDeclaration(place.Root) : null;
            ServerPackageStatus status;
            string? detail = null;
            if (place.Root is null) { status = ServerPackageStatus.Invalid; detail = "The source in manifest.json isn't a git URL or file: path."; }
            else if (declaration is null)
            {
                status = place.IsGit && link is null ? ServerPackageStatus.NotRestored : ServerPackageStatus.Missing;
                detail = status == ServerPackageStatus.NotRestored ? "Not downloaded yet. Restore or build the server to fetch it." : $"No package was found at {place.Root}.";
            }
            else if (!declaration.IsValid) { status = ServerPackageStatus.Invalid; detail = string.Join(" ", declaration.Problems); }
            else if (!string.Equals(declaration.Id, id, StringComparison.Ordinal)) { status = ServerPackageStatus.Invalid; detail = $"The folder holds {declaration.Id}, not {id}."; }
            else if (locked is null) { status = ServerPackageStatus.Changed; detail = "Not recorded in packages-lock.props yet. Run a restore."; }
            else
            {
                status = ServerPackageStatus.Ready;
                if (place.IsGit && link is null && MountService.IsWorkingClone(place.Clone))
                {
                    var head = await _git.ResolveCommitAsync(place.Clone!, "HEAD", ct).ConfigureAwait(false);
                    if (head is not null && locked.Commit is not null && !string.Equals(head, locked.Commit, StringComparison.OrdinalIgnoreCase))
                    {
                        status = ServerPackageStatus.Changed;
                        detail = $"Checked out at {Short(head)} but packages-lock.props pins {Short(locked.Commit)}. Run a restore to record the checkout.";
                    }
                    else if (await HasLocalWorkAsync(place.Clone!, ct).ConfigureAwait(false))
                    {
                        status = ServerPackageStatus.Changed;
                        detail = "Has local edits.";
                    }
                }
            }
            result.Add(new ServerPackageInfo(id, declaration?.DisplayName is { Length: > 0 } name ? name : id,
                declaration?.Version ?? locked?.Version ?? "", source, locked?.Commit, place.Root, link, status,
                declaration?.Modules is { Count: > 0 } modules ? modules : locked?.Modules ?? Array.Empty<ServerPackageModule>(), detail));
        }
        return result;
    }

    public async Task<ServerPackageResult> InstallAsync(string repoRoot, string source, string? expectedId = null, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (CheckProject(repoRoot) is { } problem) return problem;
        source = (source ?? "").Trim();
        var manifest = LoadManifest(repoRoot);
        var packagesDirectory = PackagesDirectory(repoRoot);
        Directory.CreateDirectory(packagesDirectory);

        var parsed = ParseSource(source);
        if (parsed.LocalPath is { } relative)
        {
            var root = ResolveLocal(repoRoot, relative);
            var declaration = ReadDeclaration(root);
            if (Validate(declaration, expectedId, manifest) is { } invalid) return invalid;
            manifest.Dependencies[declaration.Id] = "file:" + relative;
            SaveManifest(repoRoot, manifest);
            await WriteLockAsync(repoRoot, ct).ConfigureAwait(false);
            return ServerPackageResult.Success(declaration.Id, $"Added {declaration.DisplayName} {declaration.Version} from {root}.");
        }
        if (parsed.CloneUrl is null) return ServerPackageResult.Fail($"\"{source}\" isn't a git URL or a file: path.");
        if (!_git.IsAvailable) return ServerPackageResult.Fail("Git was not found. Install Git and make sure it is on your PATH.");

        var temp = Path.Combine(packagesDirectory, ".install-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var clone = await _git.CloneAtAsync(parsed.CloneUrl, temp, parsed.Ref, onProgress, ct).ConfigureAwait(false);
            if (!clone.Ok) return ServerPackageResult.Fail($"Clone failed: {clone.Output}");
            var root = parsed.SubPath is null ? temp : Path.Combine(temp, parsed.SubPath);
            var declaration = ReadDeclaration(root);
            if (Validate(declaration, expectedId, manifest) is { } invalid) return invalid;
            var destination = Path.Combine(packagesDirectory, declaration.Id);
            if (Directory.Exists(destination) || File.Exists(destination))
                return ServerPackageResult.Fail($"{destination} already exists. Remove it, or run a restore if it belongs to this package.", declaration.Id);
            MoveDirectory(temp, destination);
            manifest.Dependencies[declaration.Id] = parsed.ManifestValue;
            SaveManifest(repoRoot, manifest);
            await WriteLockAsync(repoRoot, ct).ConfigureAwait(false);
            return ServerPackageResult.Success(declaration.Id, $"Installed {declaration.DisplayName} {declaration.Version} into the Basis Server ({string.Join(", ", declaration.Modules.Select(m => m.Assembly).Distinct())}).");
        }
        finally
        {
            await DeleteFolderQuietlyAsync(temp).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records a git package in the manifest and lock while building it from an existing local folder (for example the
    /// Unity project's own working copy of the same package), so one checkout serves both Unity and the server.
    /// </summary>
    public async Task<ServerPackageResult> InstallLinkedAsync(string repoRoot, string source, string folder, string? expectedId = null, CancellationToken ct = default)
    {
        if (CheckProject(repoRoot) is { } problem) return problem;
        var parsed = ParseSource((source ?? "").Trim());
        if (parsed.CloneUrl is null) return ServerPackageResult.Fail($"\"{source}\" isn't a git URL.");
        var root = Path.GetFullPath(folder);
        var manifest = LoadManifest(repoRoot);
        var declaration = ReadDeclaration(root);
        if (Validate(declaration, expectedId, manifest) is { } invalid) return invalid;
        Directory.CreateDirectory(PackagesDirectory(repoRoot));
        manifest.Dependencies[declaration.Id] = parsed.ManifestValue;
        SaveManifest(repoRoot, manifest);
        var links = LoadLinks(repoRoot).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        links[declaration.Id] = root;
        SaveLinks(repoRoot, links);
        await WriteLockAsync(repoRoot, ct).ConfigureAwait(false);
        return ServerPackageResult.Success(declaration.Id, $"Installed {declaration.DisplayName} {declaration.Version} into the Basis Server, built from {root}.");
    }

    public async Task<ServerPackageResult> RemoveAsync(string repoRoot, string id, bool discardLocalWork = false, CancellationToken ct = default)
    {
        if (CheckProject(repoRoot, requireStitching: false) is { } problem) return problem;
        var manifest = LoadManifest(repoRoot);
        if (!manifest.Dependencies.TryGetValue(id, out var source)) return ServerPackageResult.Fail($"{id} isn't installed on this Basis Server.", id);
        var links = LoadLinks(repoRoot).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var place = Locate(repoRoot, id, source, null);
        if (place.IsGit && place.Clone is { } clone && Directory.Exists(clone))
        {
            if (!MountService.IsWorkingClone(clone) && !discardLocalWork)
                return ServerPackageResult.Fail($"{clone} isn't a git clone, so it was left untouched. Remove it by hand, or force the removal.", id);
            if (!discardLocalWork && await HasLocalWorkAsync(clone, ct).ConfigureAwait(false))
                return ServerPackageResult.Fail($"{id} has local edits in {clone}. Commit or discard them first, or force the removal.", id);
            await BasisInstallService.DeleteFolderAsync(clone).ConfigureAwait(false);
        }
        manifest.Dependencies.Remove(id);
        SaveManifest(repoRoot, manifest);
        if (links.Remove(id)) SaveLinks(repoRoot, links);
        await WriteLockAsync(repoRoot, ct).ConfigureAwait(false);
        return ServerPackageResult.Success(id, $"Removed {id} from the Basis Server.");
    }

    /// <summary>Moves a git package to the newest commit of its ref (or to <paramref name="newRef"/>) and records it.</summary>
    public async Task<ServerPackageResult> UpdateAsync(string repoRoot, string id, string? newRef = null, bool discardLocalWork = false, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (CheckProject(repoRoot) is { } problem) return problem;
        var manifest = LoadManifest(repoRoot);
        if (!manifest.Dependencies.TryGetValue(id, out var source)) return ServerPackageResult.Fail($"{id} isn't installed on this Basis Server.", id);
        var parsed = ParseSource(source);
        if (parsed.CloneUrl is null)
        {
            await WriteLockAsync(repoRoot, ct).ConfigureAwait(false);
            return ServerPackageResult.Success(id, $"{id} is a local package; its declaration was re-read.");
        }
        if (!GitUrlPolicy.IsSafeRef(newRef)) return ServerPackageResult.Fail($"'{newRef}' isn't a valid git ref.", id);
        var target = string.IsNullOrWhiteSpace(newRef) ? parsed.Ref : newRef.Trim();
        var links = LoadLinks(repoRoot);
        if (links.TryGetValue(id, out var link))
        {
            if (!string.IsNullOrWhiteSpace(newRef)) return ServerPackageResult.Fail($"{id} is built from {link}. Switch that folder to {newRef}, or unlink it first.", id);
            await WriteLockAsync(repoRoot, ct).ConfigureAwait(false);
            return ServerPackageResult.Success(id, $"{id} is built from {link}; recorded its current commit.");
        }

        var clone = Path.Combine(PackagesDirectory(repoRoot), id);
        if (!Directory.Exists(clone))
        {
            var cloned = await _git.CloneAtAsync(parsed.CloneUrl, clone, target, onProgress, ct).ConfigureAwait(false);
            if (!cloned.Ok) { await DeleteFolderQuietlyAsync(clone).ConfigureAwait(false); return ServerPackageResult.Fail($"Clone failed: {cloned.Output}", id); }
        }
        else
        {
            if (!MountService.IsWorkingClone(clone)) return ServerPackageResult.Fail($"{clone} isn't a git clone, so it can't be updated.", id);
            if (!discardLocalWork && await HasLocalWorkAsync(clone, ct).ConfigureAwait(false))
                return ServerPackageResult.Fail($"{id} has local edits in {clone}. Commit or discard them first, or force the update.", id);
            var fetch = await _git.FetchAsync(clone, onProgress, ct).ConfigureAwait(false);
            if (!fetch.Ok) return ServerPackageResult.Fail($"Fetch failed: {fetch.Output}", id);
            var commit = string.IsNullOrWhiteSpace(target)
                ? await _git.ResolveCommitAsync(clone, "origin/HEAD", ct).ConfigureAwait(false)
                : await _git.ResolveCommitAsync(clone, "origin/" + target, ct).ConfigureAwait(false) ?? await _git.ResolveCommitAsync(clone, target, ct).ConfigureAwait(false);
            if (commit is null) return ServerPackageResult.Fail($"{id} has no branch, tag or commit called '{target ?? "HEAD"}'.", id);
            var checkout = await _git.CheckoutDetachedAsync(clone, commit, ct).ConfigureAwait(false);
            if (!checkout.Ok) return ServerPackageResult.Fail($"Checkout failed: {checkout.Output}", id);
        }

        var root = parsed.SubPath is null ? clone : Path.Combine(clone, parsed.SubPath);
        var declaration = ReadDeclaration(root);
        if (!declaration.IsValid) return ServerPackageResult.Fail($"{id} no longer works as a server package: {string.Join(" ", declaration.Problems)}", id);
        manifest.Dependencies[id] = parsed.WithRef(target);
        SaveManifest(repoRoot, manifest);
        await WriteLockAsync(repoRoot, ct).ConfigureAwait(false);
        var head = await _git.ResolveCommitAsync(clone, "HEAD", ct).ConfigureAwait(false);
        return ServerPackageResult.Success(id, $"{declaration.DisplayName} is now {declaration.Version} ({Short(head)}).");
    }

    /// <summary>Downloads every git package that isn't on disk yet (at its locked commit) and re-records the lock.</summary>
    public async Task<ServerPackageResult> RestoreAsync(string repoRoot, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (CheckProject(repoRoot) is { } problem) return problem;
        var manifest = LoadManifest(repoRoot);
        var locks = LoadLock(repoRoot);
        var links = LoadLinks(repoRoot);
        var restored = 0;
        var failures = new List<string>();
        foreach (var (id, source) in manifest.Dependencies.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (links.ContainsKey(id)) continue;
            var parsed = ParseSource(source);
            if (parsed.CloneUrl is null) continue;
            var clone = Path.Combine(PackagesDirectory(repoRoot), id);
            if (Directory.Exists(clone)) continue;
            if (!_git.IsAvailable) return ServerPackageResult.Fail("Git was not found. Install Git and make sure it is on your PATH.");
            var checkout = locks.TryGetValue(id, out var locked) && locked.Commit is not null && string.Equals(locked.Url, parsed.CloneUrl, StringComparison.Ordinal) ? locked.Commit : parsed.Ref;
            onProgress?.Invoke($"Restoring {id}…");
            var cloned = await _git.CloneAtAsync(parsed.CloneUrl, clone, checkout, onProgress, ct).ConfigureAwait(false);
            if (cloned.Ok) restored++;
            else
            {
                await DeleteFolderQuietlyAsync(clone).ConfigureAwait(false);
                failures.Add($"{id}: {cloned.Output}");
            }
        }
        await WriteLockAsync(repoRoot, ct).ConfigureAwait(false);
        if (failures.Count > 0) return ServerPackageResult.Fail("Some server packages could not be restored:\n" + string.Join("\n", failures));
        return ServerPackageResult.Success("", restored == 0 ? "Server packages are up to date." : $"Restored {restored} server package(s).");
    }

    public async Task<ServerPackageResult> LinkAsync(string repoRoot, string id, string folder, CancellationToken ct = default)
    {
        if (CheckProject(repoRoot) is { } problem) return problem;
        var manifest = LoadManifest(repoRoot);
        if (!manifest.Dependencies.TryGetValue(id, out var source)) return ServerPackageResult.Fail($"{id} isn't installed on this Basis Server.", id);
        if (ParseSource(source).CloneUrl is null) return ServerPackageResult.Fail($"{id} already comes from a local folder ({source}).", id);
        var root = Path.GetFullPath(folder);
        var declaration = ReadDeclaration(root);
        if (!declaration.IsValid) return ServerPackageResult.Fail(string.Join(" ", declaration.Problems), id);
        if (!string.Equals(declaration.Id, id, StringComparison.Ordinal)) return ServerPackageResult.Fail($"{root} holds {declaration.Id}, not {id}.", id);
        var links = LoadLinks(repoRoot).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        links[id] = root;
        SaveLinks(repoRoot, links);
        await WriteLockAsync(repoRoot, ct).ConfigureAwait(false);
        return ServerPackageResult.Success(id, $"The server now builds {id} from {root}.");
    }

    public async Task<ServerPackageResult> UnlinkAsync(string repoRoot, string id, CancellationToken ct = default)
    {
        if (CheckProject(repoRoot, requireStitching: false) is { } problem) return problem;
        var links = LoadLinks(repoRoot).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (!links.Remove(id)) return ServerPackageResult.Fail($"{id} isn't linked to a local folder.", id);
        SaveLinks(repoRoot, links);
        await WriteLockAsync(repoRoot, ct).ConfigureAwait(false);
        var restored = Directory.Exists(Path.Combine(PackagesDirectory(repoRoot), id));
        return ServerPackageResult.Success(id, restored
            ? $"The server builds {id} from its own copy again."
            : $"{id} is no longer linked. Restore or build the server to download its own copy.");
    }

    /// <summary>Regenerates <c>packages-lock.props</c> from the manifest, what is on disk, and the previous lock.</summary>
    public async Task WriteLockAsync(string repoRoot, CancellationToken ct = default)
    {
        var manifest = LoadManifest(repoRoot);
        var previous = LoadLock(repoRoot);
        var links = LoadLinks(repoRoot);
        var entries = new List<ServerPackageLockEntry>();
        foreach (var (id, source) in manifest.Dependencies.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!IsValidId(id)) continue;
            previous.TryGetValue(id, out var old);
            links.TryGetValue(id, out var link);
            var parsed = ParseSource(source);
            var place = Locate(repoRoot, id, source, link);
            if (place.Root is null) continue;
            var declaration = File.Exists(Path.Combine(place.Root, "package.json")) ? ReadDeclaration(place.Root) : null;
            var usable = declaration is { IsValid: true } && string.Equals(declaration.Id, id, StringComparison.Ordinal);
            string? commit = null;
            if (place.IsGit)
            {
                if (link is not null) commit = await LinkedCommitAsync(link, parsed.CloneUrl!, ct).ConfigureAwait(false);
                else if (MountService.IsWorkingClone(place.Clone)) commit = await _git.ResolveCommitAsync(place.Clone!, "HEAD", ct).ConfigureAwait(false);
                if (commit is null && old is not null && string.Equals(old.Url, parsed.CloneUrl, StringComparison.Ordinal)) commit = old.Commit;
            }
            entries.Add(new ServerPackageLockEntry(id,
                usable ? declaration!.Version : old?.Version ?? "",
                source, parsed.CloneUrl, parsed.Ref, commit, parsed.SubPath, parsed.LocalPath,
                usable ? declaration!.Modules : old?.Modules ?? Array.Empty<ServerPackageModule>(),
                usable ? declaration!.NuGet : old?.NuGet ?? new Dictionary<string, string>()));
        }
        Directory.CreateDirectory(PackagesDirectory(repoRoot));
        AtomicFile.WriteAllText(LockPath(repoRoot), BuildLock(entries));
    }

    public static string BuildLock(IReadOnlyList<ServerPackageLockEntry> entries)
    {
        var project = new XElement("Project");
        var properties = new XElement("PropertyGroup");
        var data = new XElement("ItemGroup");
        var hosts = HostAssemblies.ToDictionary(h => h, h => new XElement("ItemGroup", new XAttribute("Condition", $"'$(MSBuildProjectName)' == '{h}'")), StringComparer.Ordinal);
        var references = HostAssemblies.ToDictionary(h => h, _ => new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var key = PropertyKey(entry.Id);
            var rootProperty = "BasisServerPackageRoot_" + key;
            var defaultRoot = "$(MSBuildThisFileDirectory)" + (entry.LocalPath is { } local
                ? EscapeSegments(local) + "/"
                : entry.Id + "/" + (entry.SubPath is { Length: > 0 } sub ? EscapeSegments(sub) + "/" : ""));
            properties.Add(new XElement(rootProperty, new XAttribute("Condition", $"'$({rootProperty})' == ''"), defaultRoot));
            data.Add(new XElement("BasisServerPackage",
                new XAttribute("Include", entry.Id),
                new XAttribute("Version", Escape(entry.Version)),
                new XAttribute("Source", Escape(entry.Source)),
                new XAttribute("Url", Escape(entry.Url ?? "")),
                new XAttribute("Ref", Escape(entry.Ref ?? "")),
                new XAttribute("Commit", entry.Commit ?? ""),
                new XAttribute("SubPath", Escape(entry.SubPath ?? "")),
                new XAttribute("LocalPath", Escape(entry.LocalPath ?? "")),
                new XAttribute("Clone", entry.Url is null ? "" : $"$(MSBuildThisFileDirectory){entry.Id}/"),
                new XAttribute("Root", $"$({rootProperty})"),
                new XAttribute("Linked", $"$(BasisServerPackageLinked_{key})")));
            foreach (var module in entry.Modules)
            {
                data.Add(new XElement("BasisServerPackageModule",
                    new XAttribute("Include", entry.Id),
                    new XAttribute("Path", Escape(module.Path)),
                    new XAttribute("Assembly", module.Assembly),
                    new XAttribute("Excludes", Escape(string.Join('|', module.Excludes)))));
                if (!hosts.TryGetValue(module.Assembly, out var group)) continue;
                var folder = $"$({rootProperty})" + (module.Path.Length == 0 ? "" : EscapeSegments(module.Path) + "/");
                var excluded = new List<string> { folder + "**/obj/**", folder + "**/bin/**", folder + "**/.*/**", folder + "**/*~/**" };
                excluded.AddRange(module.Excludes.Select(x => folder + EscapeSegments(x) + "/**"));
                group.Add(new XElement("Compile",
                    new XAttribute("Include", folder + "**/*.cs"),
                    new XAttribute("Exclude", string.Join(';', excluded)),
                    new XAttribute("LinkBase", "Packages/" + entry.Id + (module.Path.Length == 0 ? "" : "/" + EscapeSegments(module.Path)))));
            }
            foreach (var (package, version) in entry.NuGet.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                data.Add(new XElement("BasisServerPackageNuGet", new XAttribute("Include", entry.Id), new XAttribute("Package", package), new XAttribute("Version", Escape(version))));
            foreach (var host in entry.Modules.Select(m => m.Assembly).Distinct(StringComparer.Ordinal).Where(hosts.ContainsKey))
            {
                hosts[host].Add(new XElement("BasisServerPackageMissing",
                    new XAttribute("Include", entry.Id),
                    new XAttribute("Root", $"$({rootProperty})"),
                    new XAttribute("Condition", $"!Exists('$({rootProperty})package.json')")));
                foreach (var (package, version) in entry.NuGet)
                    if (!references[host].TryGetValue(package, out var existing) || CompareVersions(version, existing) > 0)
                        references[host][package] = version;
            }
            hosts["BasisNetworkConsole"].Add(new XElement("AssemblyMetadata",
                new XAttribute("Include", MetadataPrefix + entry.Id),
                new XAttribute("Value", Escape($"{entry.Version} ({(entry.Url is null ? "local" : Short(entry.Commit) is { Length: > 0 } shortCommit ? shortCommit : "unpinned")})".TrimStart()))));
        }
        foreach (var (host, packages) in references)
            foreach (var (package, version) in packages)
                hosts[host].Add(new XElement("PackageReference", new XAttribute("Include", package), new XAttribute("Version", Escape(version))));
        if (properties.HasElements) project.Add(properties);
        if (data.HasElements) project.Add(data);
        foreach (var host in HostAssemblies)
            if (hosts[host].HasElements) project.Add(hosts[host]);

        var document = new XDocument(new XComment(" Generated by the Basis Package Manager from manifest.json. Edit the manifest, then run a server package restore. "), project);
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, IndentChars = "  ", NewLineChars = "\n", NewLineHandling = NewLineHandling.Replace }))
            document.Save(writer);
        return builder.Append('\n').ToString();
    }

    public static string PropertyKey(string id)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..6].ToLowerInvariant();
        return Regex.Replace(id, "[^A-Za-z0-9]", "_") + "_" + hash;
    }

    private void SaveLinks(string repoRoot, IReadOnlyDictionary<string, string> links)
    {
        var path = LocalPath(repoRoot);
        if (links.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var properties = new XElement("PropertyGroup");
        var items = new XElement("ItemGroup");
        foreach (var (id, folder) in links.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var key = PropertyKey(id);
            var root = EscapeSegments(Path.GetFullPath(folder).Replace('\\', '/').TrimEnd('/')) + "/";
            properties.Add(new XElement("BasisServerPackageRoot_" + key, root));
            properties.Add(new XElement("BasisServerPackageLinked_" + key, "true"));
            items.Add(new XElement("BasisServerPackageLink", new XAttribute("Include", id), new XAttribute("Folder", Escape(Path.GetFullPath(folder)))));
        }
        var document = new XDocument(new XComment(" This machine's server package folder links (Basis Package Manager). Not committed. "), new XElement("Project", properties, items));
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, IndentChars = "  ", NewLineChars = "\n", NewLineHandling = NewLineHandling.Replace }))
            document.Save(writer);
        AtomicFile.WriteAllText(path, builder.Append('\n').ToString());
    }

    private async Task<string?> LinkedCommitAsync(string folder, string cloneUrl, CancellationToken ct)
    {
        try
        {
            var top = await _git.GetTopLevelAsync(folder, ct).ConfigureAwait(false);
            if (top is null) return null;
            var origin = await _git.GetRemoteUrlAsync(top, "origin", ct).ConfigureAwait(false);
            if (origin is null || !SameRepository(origin, cloneUrl)) return null;
            return await _git.ResolveCommitAsync(top, "HEAD", ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) { DiagnosticLog.Write($"Reading the commit of linked server package folder {folder}", ex); return null; }
    }

    private static bool SameRepository(string a, string b)
    {
        static string Normalize(string url)
        {
            var value = (UpmGitUrl.Parse(url)?.CloneUrl ?? url).Trim().Replace('\\', '/').TrimEnd('/');
            if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
            return value.ToLowerInvariant();
        }
        return string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);
    }

    private async Task<bool> HasLocalWorkAsync(string folder, CancellationToken ct)
    {
        if (!MountService.IsWorkingClone(folder)) return false;
        var status = await _git.GetStatusAsync(folder, ct).ConfigureAwait(false);
        return status.ChangeCount > 0 || await _git.HasUnpublishedWorkAsync(folder, ct).ConfigureAwait(false);
    }

    private ServerPackageResult? CheckProject(string repoRoot, bool requireStitching = true)
    {
        if (!HasServer(repoRoot)) return ServerPackageResult.Fail("This project has no Basis Server source (Basis Server/BasisNetworkCore).");
        if (requireStitching && !SupportsPackages(repoRoot))
            return ServerPackageResult.Fail("This Basis Server can't build packages yet. Update Basis to a version whose Basis Server/Directory.Build.targets imports Packages/packages-lock.props.");
        return null;
    }

    private static ServerPackageResult? Validate(ServerPackageDeclaration declaration, string? expectedId, ServerPackageManifest manifest)
    {
        if (!declaration.IsValid) return ServerPackageResult.Fail(string.Join(" ", declaration.Problems), Blank(declaration.Id));
        if (!string.IsNullOrWhiteSpace(expectedId) && !string.Equals(expectedId, declaration.Id, StringComparison.Ordinal))
            return ServerPackageResult.Fail($"The repository holds {declaration.Id}, not {expectedId}.", declaration.Id);
        if (manifest.Dependencies.ContainsKey(declaration.Id))
            return ServerPackageResult.Fail($"{declaration.Id} is already installed on this Basis Server. Update or remove it instead.", declaration.Id);
        return null;
    }

    private (string? Root, string? Clone, bool IsGit) Locate(string repoRoot, string id, string source, string? link)
    {
        var parsed = ParseSource(source);
        if (parsed.LocalPath is { } relative) return (ResolveLocal(repoRoot, relative), null, false);
        if (parsed.CloneUrl is null || !IsValidId(id)) return (null, null, false);
        var clone = Path.Combine(PackagesDirectory(repoRoot), id);
        var root = link ?? (parsed.SubPath is null ? clone : Path.Combine(clone, parsed.SubPath.Replace('/', Path.DirectorySeparatorChar)));
        return (root, clone, true);
    }

    private static string ResolveLocal(string repoRoot, string relative) =>
        Path.GetFullPath(Path.Combine(PackagesDirectory(repoRoot), relative.Replace('/', Path.DirectorySeparatorChar)));

    private ParsedSource ParseSource(string source)
    {
        var raw = (source ?? "").Trim();
        if (raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var relative = raw[5..].Trim().Replace('\\', '/');
            return relative.Length == 0 || relative.Any(char.IsControl) ? default : new ParsedSource(null, null, null, relative.TrimEnd('/'), raw);
        }
        var parsed = UpmGitUrl.Parse(raw);
        if (parsed is not null && _git.CanFetch(parsed.CloneUrl) && GitUrlPolicy.IsSafeRef(parsed.Ref) && GitUrlPolicy.IsSafeSubPath(parsed.Path))
            return new ParsedSource(parsed.CloneUrl, parsed.Ref, parsed.Path, null, parsed.ToManifestUrl(parsed.Ref, parsed.Path));
        var hash = raw.IndexOf('#');
        var url = hash >= 0 ? raw[..hash] : raw;
        var gitRef = hash >= 0 ? Blank(raw[(hash + 1)..].Trim()) : null;
        if (!url.Contains('?') && _git.CanFetch(url) && GitUrlPolicy.IsSafeRef(gitRef))
            return new ParsedSource(url, gitRef, null, null, raw);
        return default;
    }

    private readonly record struct ParsedSource(string? CloneUrl, string? Ref, string? SubPath, string? LocalPath, string ManifestValue)
    {
        public string WithRef(string? gitRef)
        {
            var parsed = UpmGitUrl.Parse(ManifestValue);
            if (parsed is not null && CloneUrl is not null && string.Equals(parsed.CloneUrl, CloneUrl, StringComparison.Ordinal)) return parsed.ToManifestUrl(gitRef, SubPath);
            return string.IsNullOrWhiteSpace(gitRef) ? CloneUrl ?? ManifestValue : $"{CloneUrl}#{gitRef}";
        }
    }

    private static void Discover(string packageRoot, List<(string Path, string Assembly)> found)
    {
        foreach (var asmref in Walk(packageRoot).SelectMany(d => SafeFiles(d, "*.asmref")))
        {
            var host = ResolveHost(ReadAsmrefReference(asmref));
            if (host is null) continue;
            var folder = Path.GetRelativePath(packageRoot, Path.GetDirectoryName(asmref)!).Replace('\\', '/');
            found.Add((folder == "." ? "" : folder, host));
        }
        var serverOnly = Path.Combine(packageRoot, "Server~");
        foreach (var host in HostAssemblies)
            if (Directory.Exists(Path.Combine(serverOnly, host))) found.Add(("Server~/" + host, host));
    }

    private static IReadOnlyList<string> Excludes(string packageRoot, string modulePath, IReadOnlyList<(string Path, string Assembly)> modules)
    {
        var moduleFolder = Path.Combine(packageRoot, modulePath);
        var excluded = new List<string>();
        foreach (var other in modules)
            if (other.Path.Length > modulePath.Length && (modulePath.Length == 0 || other.Path.StartsWith(modulePath + "/", StringComparison.Ordinal)))
                excluded.Add(modulePath.Length == 0 ? other.Path : other.Path[(modulePath.Length + 1)..]);
        var pending = new Stack<string>(SafeDirectories(moduleFolder));
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            var name = Path.GetFileName(folder);
            if (name.StartsWith('.') || name.EndsWith('~') || SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (SafeFiles(folder, "*.asmdef").Any() || SafeFiles(folder, "*.asmref").Any())
            {
                excluded.Add(Path.GetRelativePath(moduleFolder, folder).Replace('\\', '/'));
                continue;
            }
            foreach (var child in SafeDirectories(folder)) pending.Push(child);
        }
        return excluded.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> Walk(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            yield return folder;
            foreach (var child in SafeDirectories(folder))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || name.EndsWith('~') || SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                pending.Push(child);
            }
        }
    }

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.GetDirectories(folder) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFiles(string folder, string pattern)
    {
        try { return Directory.GetFiles(folder, pattern); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static string? ReadAsmrefReference(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), PackageJson);
            return document.RootElement.ValueKind == JsonValueKind.Object ? Text(document.RootElement, "reference") : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? ResolveHost(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        if (reference.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase)) return HostGuids.GetValueOrDefault(reference[5..].Trim());
        return HostAssemblies.FirstOrDefault(h => h.Equals(reference.Trim(), StringComparison.Ordinal));
    }

    private static int CompareVersions(string a, string b)
    {
        if (SemVer.TryParse(a, out var left) && SemVer.TryParse(b, out var right)) return left.CompareTo(right);
        if (Version.TryParse(a, out var x) && Version.TryParse(b, out var y)) return x.CompareTo(y);
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static void MoveDirectory(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { Directory.Move(from, to); return; }
            catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(100 * attempt); }
        }
    }

    private static async Task DeleteFolderQuietlyAsync(string folder)
    {
        try { if (Directory.Exists(folder)) await BasisInstallService.DeleteFolderAsync(folder).ConfigureAwait(false); }
        catch (Exception ex) { DiagnosticLog.Write($"Cleaning up {folder}", ex); }
    }

    private static XDocument? LoadXml(string path)
    {
        if (!File.Exists(path)) return null;
        try { return XDocument.Load(path); }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Reading {path}", ex);
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Attr(XElement element, string name) => element.Attribute(name)?.Value ?? "";

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Short(string? commit) => commit is { Length: > 7 } ? commit[..7] : commit ?? "";

    private static string EscapeSegments(string path) => Escape(path.Replace('\\', '/'));

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '%' => "%25",
                '$' => "%24",
                '@' => "%40",
                '\'' => "%27",
                ';' => "%3B",
                '?' => "%3F",
                '*' => "%2A",
                _ => c.ToString(),
            });
        }
        return builder.ToString();
    }

    private static string Unescape(string value) => value.Contains('%') ? Uri.UnescapeDataString(value) : value;
}
