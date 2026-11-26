using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class EventServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 12, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LoadEmbedded_returns_an_empty_feed()
    {
        Assert.Empty(EventService.LoadEmbedded());
    }

    [Fact]
    public async Task LoadAsync_orders_soonest_first()
    {
        const string json = """
        [
          { "id": "later", "start": "2026-12-20T19:00:00Z" },
          { "id": "sooner", "start": "2026-12-14T19:00:00Z" },
          { "id": "offset", "start": "2026-12-14T19:00:00+02:00" }
        ]
        """;
        var svc = new EventService(StubHttpMessageHandler.Always(json));

        var items = await svc.LoadAsync("https://example.com/events.json", Now);

        Assert.Equal(new[] { "offset", "sooner", "later" }, items.Select(e => e.Id).ToArray());
    }

    [Fact]
    public async Task LoadAsync_drops_finished_and_undated_events()
    {
        const string json = """
        [
          { "id": "over", "start": "2026-12-09T19:00:00Z", "end": "2026-12-09T21:00:00Z" },
          { "id": "nodate" },
          { "id": "garbage", "start": "soon" },
          { "id": "next", "start": "2026-12-11T19:00:00Z" }
        ]
        """;
        var svc = new EventService(StubHttpMessageHandler.Always(json));

        var items = await svc.LoadAsync(null, Now);

        Assert.Equal(new[] { "next" }, items.Select(e => e.Id).ToArray());
    }

    [Fact]
    public async Task LoadAsync_keeps_an_event_that_is_running_now()
    {
        const string json = """
        [
          { "id": "running", "start": "2026-12-10T11:00:00Z", "end": "2026-12-10T13:00:00Z" },
          { "id": "noend-running", "start": "2026-12-10T10:00:00Z" },
          { "id": "noend-over", "start": "2026-12-10T08:00:00Z" }
        ]
        """;
        var svc = new EventService(StubHttpMessageHandler.Always(json));

        var items = await svc.LoadAsync(null, Now);

        Assert.Equal(new[] { "noend-running", "running" }, items.Select(e => e.Id).ToArray());
    }

    [Fact]
    public async Task LoadAsync_moves_a_weekly_event_to_its_next_occurrence()
    {
        const string json = """
        [
          { "id": "weekly", "start": "2026-06-13T20:00:00Z", "end": "2026-06-13T21:00:00Z", "repeat": "weekly" },
          { "id": "once", "start": "2026-06-13T20:00:00Z", "end": "2026-06-13T21:00:00Z" }
        ]
        """;
        var svc = new EventService(StubHttpMessageHandler.Always(json));

        var items = await svc.LoadAsync(null, Now);

        var weekly = Assert.Single(items);
        Assert.Equal("weekly", weekly.Id);
        Assert.Equal(new DateTimeOffset(2026, 12, 12, 20, 0, 0, TimeSpan.Zero), EventService.ParseTime(weekly.Start));
        Assert.Equal(new DateTimeOffset(2026, 12, 12, 21, 0, 0, TimeSpan.Zero), EventService.ParseTime(weekly.End));
    }

    [Fact]
    public async Task LoadAsync_keeps_a_weekly_event_on_the_occurrence_running_now()
    {
        const string json = """
        [
          { "id": "weekly", "start": "2026-12-03T11:00:00Z", "end": "2026-12-03T13:00:00Z", "repeat": "weekly" }
        ]
        """;
        var svc = new EventService(StubHttpMessageHandler.Always(json));

        var items = await svc.LoadAsync(null, Now);

        Assert.Equal(new DateTimeOffset(2026, 12, 10, 11, 0, 0, TimeSpan.Zero), EventService.ParseTime(Assert.Single(items).Start));
    }

    [Fact]
    public async Task LoadAsync_falls_back_to_embedded_when_offline()
    {
        var svc = new EventService(StubHttpMessageHandler.AlwaysThrows());
        var items = await svc.LoadAsync("https://example.com/events.json", Now);
        Assert.Empty(items);
    }
}
