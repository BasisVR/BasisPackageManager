using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class AnnouncementServiceTests
{
    [Fact]
    public void LoadEmbedded_returns_an_empty_feed()
    {
        // No announcements are baked into the app — they come only from the live feed.
        Assert.Empty(AnnouncementService.LoadEmbedded());
    }

    [Fact]
    public async Task LoadAsync_orders_pinned_first_then_newest_date()
    {
        const string json = """
        [
          { "id": "a", "date": "2026-01-01", "pinned": false, "title": "A" },
          { "id": "b", "date": "2025-01-01", "pinned": true,  "title": "B" },
          { "id": "c", "date": "2026-02-01", "pinned": false, "title": "C" }
        ]
        """;
        var svc = new AnnouncementService(StubHttpMessageHandler.Always(json));

        var items = await svc.LoadAsync("https://example.com/announcements.json");

        Assert.Equal(new[] { "b", "c", "a" }, items.Select(a => a.Id).ToArray());
    }

    [Fact]
    public async Task LoadAsync_breaks_date_ties_by_id_descending()
    {
        const string json = """
        [
          { "id": "x", "date": "2026-01-01", "pinned": false },
          { "id": "z", "date": "2026-01-01", "pinned": false }
        ]
        """;
        var svc = new AnnouncementService(StubHttpMessageHandler.Always(json));

        var items = await svc.LoadAsync(null);

        Assert.Equal(new[] { "z", "x" }, items.Select(a => a.Id).ToArray());
    }

    [Fact]
    public async Task LoadAsync_sorts_undated_items_last()
    {
        const string json = """
        [
          { "id": "nodate", "pinned": false },
          { "id": "dated", "date": "2026-01-01", "pinned": false }
        ]
        """;
        var svc = new AnnouncementService(StubHttpMessageHandler.Always(json));

        var items = await svc.LoadAsync(null);

        Assert.Equal(new[] { "dated", "nodate" }, items.Select(a => a.Id).ToArray());
    }

    [Fact]
    public async Task LoadAsync_falls_back_to_embedded_when_offline()
    {
        // Offline falls back to the embedded feed, which is now empty (no baked-in announcements).
        var svc = new AnnouncementService(StubHttpMessageHandler.AlwaysThrows());
        var items = await svc.LoadAsync("https://example.com/announcements.json");
        Assert.Empty(items);
    }
}
