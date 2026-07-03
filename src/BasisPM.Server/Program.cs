using BasisPM.Server.Models;
using BasisPM.Server.Services;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
builder.Services.AddSingleton(new PackageStore(dataDir));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

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

api.MapPost("/packages", (PackageStore store, RegistrySubmission sub) =>
{
    try
    {
        var pkg = store.Upsert(sub);
        return Results.Created($"/api/packages/{pkg.Id}", pkg);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapFallbackToFile("index.html");

app.Run();
