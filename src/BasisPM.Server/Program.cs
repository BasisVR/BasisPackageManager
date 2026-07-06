using System.Text.Json;
using System.Text.Json.Serialization;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Server.Models;
using BasisPM.Server.Services;

// Static-site generator mode: `dotnet run --project src/BasisPM.Server -- generate <outDir>`
// Emits index.html + packages.json + catalog.json + packagelists.json (+ legacy bundles.json) for GitHub Pages (no server required).
if (args.Length >= 1 && args[0].Equals("generate", StringComparison.OrdinalIgnoreCase))
{
    await GenerateStaticSiteAsync(args.Length >= 2 ? args[1] : "dist");
    return;
}

var builder = WebApplication.CreateBuilder(args);

var dataDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
var seedPath = Path.Combine(builder.Environment.ContentRootPath, "seed", "packages.json");
var store = new PackageStore(dataDir, seedPath);
store.ResolveImages(Path.Combine(builder.Environment.ContentRootPath, "wwwroot", "icons"));
builder.Services.AddSingleton(store);

var packageListSeed = Path.Combine(builder.Environment.ContentRootPath, "seed", "packagelists.json");
if (!File.Exists(packageListSeed))
    packageListSeed = Path.Combine(builder.Environment.ContentRootPath, "seed", "bundles.json"); // legacy seed name
builder.Services.AddSingleton(new PackageListStore(packageListSeed));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Static-equivalent feeds — the same files the generator writes for GitHub Pages,
// so the browse UI works identically whether hosted live or as static files.
app.MapGet("/packages.json", (PackageStore store) => Results.Ok(store.All()));
app.MapGet("/catalog.json", (PackageStore store) => Results.Ok(store.ToCatalog()));
app.MapGet("/packagelists.json", (PackageListStore packageLists) => Results.Ok(packageLists.All()));
app.MapGet("/bundles.json", (PackageListStore packageLists) => Results.Ok(packageLists.All())); // legacy alias

var api = app.MapGroup("/api");

api.MapGet("/packages", (PackageStore store, string? search, string? source, string? category, string? sort)
    => Results.Ok(store.Query(search, source, category, sort)));

api.MapGet("/packages/{id}", (PackageStore store, string id) =>
{
    var pkg = store.Get(id);
    return pkg is null ? Results.NotFound() : Results.Ok(pkg);
});

api.MapGet("/categories", (PackageStore store) => Results.Ok(store.Categories()));

// Core.Catalog-compatible feed consumed by the desktop Basis Package Manager (Settings → Catalog URL).
api.MapGet("/catalog", (PackageStore store) => Results.Ok(store.ToCatalog()));

api.MapGet("/packagelists", (PackageListStore packageLists) => Results.Ok(packageLists.All()));
api.MapGet("/packagelists/{id}", (PackageListStore packageLists, string id) =>
{
    var pl = packageLists.Get(id);
    return pl is null ? Results.NotFound() : Results.Ok(pl);
});
// Legacy aliases — pre-rename desktop apps and shared links still hit /api/bundles.
api.MapGet("/bundles", (PackageListStore packageLists) => Results.Ok(packageLists.All()));
api.MapGet("/bundles/{id}", (PackageListStore packageLists, string id) =>
{
    var pl = packageLists.Get(id);
    return pl is null ? Results.NotFound() : Results.Ok(pl);
});

// The write endpoint is OFF by default: it persists to disk and its data is served to every client
// and desktop app, so an open, unauthenticated POST is a registry-poisoning / package-hijack surface.
// Enable it deliberately with BASISPM_ENABLE_SUBMISSIONS=1, and/or require a shared secret with
// BASISPM_SUBMIT_TOKEN (clients send it as the X-Submit-Token header). Curated data always comes from
// the PR-reviewed seed, never this endpoint.
var submitToken = Environment.GetEnvironmentVariable("BASISPM_SUBMIT_TOKEN");
var submissionsEnabled = IsTrue(Environment.GetEnvironmentVariable("BASISPM_ENABLE_SUBMISSIONS"))
                         || !string.IsNullOrEmpty(submitToken);

if (submissionsEnabled)
{
    api.MapPost("/packages", (PackageStore store, RegistrySubmission sub, HttpContext ctx) =>
    {
        if (!string.IsNullOrEmpty(submitToken))
        {
            var provided = ctx.Request.Headers["X-Submit-Token"].ToString();
            if (!TokensMatch(provided, submitToken))
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }
        try
        {
            var pkg = store.Upsert(sub);
            return Results.Created($"/api/packages/{pkg.Id}", pkg);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    });
}

app.MapFallbackToFile("index.html");

app.Run();

static async Task GenerateStaticSiteAsync(string outDir)
{
    var baseDir = AppContext.BaseDirectory;
    var seedPath = ResolveUp(baseDir, Path.Combine("seed", "packages.json"));
    var indexPath = ResolveUp(baseDir, Path.Combine("wwwroot", "index.html"));
    if (seedPath is null) { Console.Error.WriteLine("Could not find seed/packages.json"); Environment.Exit(1); }
    if (indexPath is null) { Console.Error.WriteLine("Could not find wwwroot/index.html"); Environment.Exit(1); }

    var packages = PackageStore.LoadSeed(seedPath);

    // Packages present in the Basis developer-branch manifest ship with Basis → mark them "built-in".
    // This wins over the official/community derivation for the source badge.
    var builtInIds = await FetchBuiltInIdsAsync(Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
    foreach (var p in packages)
        if (builtInIds.Contains(p.Id)) p.Source = "built-in";
    Console.WriteLine($"  built-in (in developer manifest): {packages.Count(p => p.Source == "built-in")} of {packages.Count}");

    // Bake real stars / forks / last-updated from GitHub/GitLab into the static bundle.
    var stats = new RepoStatsService(Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
    var github = new GitHubService();
    foreach (var p in packages)
    {
        var s = await stats.FetchAsync(p.RepoUrl ?? p.GitUrl);

        // Read the package's package.json once (authoritative for UPM): version + license.
        var upm = await FetchUpmPackageAsync(github, p.GitUrl ?? p.RepoUrl);
        if (!string.IsNullOrWhiteSpace(upm?.Version)) p.Version = upm!.Version;
        // License: an explicit seed value wins; otherwise take what the package declares.
        if (string.IsNullOrWhiteSpace(p.License) && !string.IsNullOrWhiteSpace(upm?.License)) p.License = upm!.License;

        if (s is not null)
        {
            p.Stars = s.Stars;
            p.Forks = s.Forks;
            if (!string.IsNullOrWhiteSpace(s.Updated))
                p.Updated = s.Updated.Length >= 10 ? s.Updated[..10] : s.Updated!;
            if (string.IsNullOrWhiteSpace(p.Description) && !string.IsNullOrWhiteSpace(s.Description))
                p.Description = s.Description!;
        }
        Console.WriteLine($"  · {p.Id}: {p.Stars} stars, {p.Forks} forks, v{p.Version}, {p.License ?? "no license"}");
    }

    Directory.CreateDirectory(outDir);

    // Self-hosted package images: copy wwwroot/icons → <out>/icons and point each package at its file.
    var iconsDir = Path.Combine(Path.GetDirectoryName(indexPath!)!, "icons");
    if (Directory.Exists(iconsDir))
    {
        var destIcons = Path.Combine(outDir, "icons");
        Directory.CreateDirectory(destIcons);
        foreach (var f in Directory.EnumerateFiles(iconsDir))
            File.Copy(f, Path.Combine(destIcons, Path.GetFileName(f)), overwrite: true);
        PackageStore.ResolveImages(packages, iconsDir);
    }

    var opts = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    File.WriteAllText(Path.Combine(outDir, "packages.json"), JsonSerializer.Serialize(packages, opts));
    File.WriteAllText(Path.Combine(outDir, "catalog.json"), JsonSerializer.Serialize(PackageStore.BuildCatalog(packages), opts));

    // Curated package lists for the website's Package Lists view + install-back.
    var packageListSeed = ResolveUp(baseDir, Path.Combine("seed", "packagelists.json"))
                       ?? ResolveUp(baseDir, Path.Combine("seed", "bundles.json")); // legacy seed name
    var packageLists = PackageListStore.LoadSeed(packageListSeed);
    // "Basis Recommended" always holds every registry package — refill it from the current list so
    // it can never drift behind newly-added packages.
    var recommended = packageLists.FirstOrDefault(pl => pl.Id == "basis-recommended");
    if (recommended is not null)
        recommended.Packages = packages
            .Where(p => !string.IsNullOrWhiteSpace(p.GitUrl))
            .Select(p => new PackageListEntry { Id = p.Id, Name = p.Name, GitUrl = p.GitUrl })
            .ToList();
    var packageListsJson = JsonSerializer.Serialize(packageLists, opts);
    File.WriteAllText(Path.Combine(outDir, "packagelists.json"), packageListsJson);
    File.WriteAllText(Path.Combine(outDir, "bundles.json"), packageListsJson); // legacy filename for pre-rename clients

    File.Copy(indexPath, Path.Combine(outDir, "index.html"), overwrite: true);

    Console.WriteLine($"Generated static registry ({packages.Count} packages, {packageLists.Count} package lists) → {Path.GetFullPath(outDir)}");
}

static async Task<UpmPackageJson?> FetchUpmPackageAsync(GitHubService github, string? gitOrRepoUrl)
{
    if (string.IsNullOrWhiteSpace(gitOrRepoUrl)) return null;
    try
    {
        var loc = GitHubService.Parse(gitOrRepoUrl);
        return await github.FetchPackageJsonAsync(loc);
    }
    catch { return null; }
}

// The set of package ids declared in the Basis developer-branch Packages/manifest.json — these ship
// with Basis and are badged "built-in". Repo/branch/path are overridable via env for forks/renames.
static async Task<HashSet<string>> FetchBuiltInIdsAsync(string? token)
{
    var repo = Environment.GetEnvironmentVariable("BASIS_MANIFEST_REPO") ?? "BasisVR/Basis";
    var branch = Environment.GetEnvironmentVariable("BASIS_MANIFEST_BRANCH") ?? "developer";
    var path = Environment.GetEnvironmentVariable("BASIS_MANIFEST_PATH") ?? "Basis/Packages/manifest.json";
    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BasisPackageManager/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.raw");
        if (!string.IsNullOrWhiteSpace(token))
            http.DefaultRequestHeaders.Authorization = new("Bearer", token.Trim());
        var url = $"https://api.github.com/repos/{repo}/contents/{Uri.EscapeDataString(path).Replace("%2F", "/")}?ref={Uri.EscapeDataString(branch)}";
        var json = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
            foreach (var dep in deps.EnumerateObject())
                ids.Add(dep.Name);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  (built-in detection skipped — could not read {repo}@{branch}: {ex.Message})");
    }
    return ids;
}

static bool IsTrue(string? v) =>
    v is not null && (v == "1"
        || v.Equals("true", StringComparison.OrdinalIgnoreCase)
        || v.Equals("yes", StringComparison.OrdinalIgnoreCase));

// Constant-time compare so a wrong token can't be recovered byte-by-byte via response timing.
static bool TokensMatch(string? provided, string expected) =>
    System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(provided ?? ""),
        System.Text.Encoding.UTF8.GetBytes(expected));

static string? ResolveUp(string start, string relative)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, relative);
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    return null;
}

// Makes the implicit top-level Program class public so the integration-test project can
// drive the real app through WebApplicationFactory<Program>. Behaviourally inert.
public partial class Program { }
