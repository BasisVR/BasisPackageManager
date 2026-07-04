using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class AnnouncementServiceTests
{
    [Fact]
    public void LoadEmbedded_returns_the_baked_in_feed()
    {
        var items = AnnouncementService.LoadEmbedded();
        Assert.NotEmpty(items);
        Assert.Contains(items, a => a.Id == "2026-07-04-early-preview" && a.Pinned);
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
        var svc = new AnnouncementService(StubHttpMessageHandler.AlwaysThrows());
        var items = await svc.LoadAsync("https://example.com/announcements.json");
        Assert.Contains(items, a => a.Id == "2026-07-04-early-preview");
    }
}
