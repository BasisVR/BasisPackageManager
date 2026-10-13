using System.Net;
using System.Net.Http.Headers;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class HttpRetryHandlerTests
{
    private static readonly TimeSpan[] NoWait = { TimeSpan.Zero, TimeSpan.Zero };

    private static (HttpClient Client, StubHttpMessageHandler Stub) Client(params Func<HttpResponseMessage>[] replies)
    {
        var next = 0;
        var stub = new StubHttpMessageHandler(_ => replies[Math.Min(next++, replies.Length - 1)]());
        return (new HttpClient(new HttpRetryHandler(stub, NoWait)), stub);
    }

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    private static HttpResponseMessage TooMany(TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    [Fact]
    public async Task A_server_error_is_retried_until_it_clears()
    {
        var (client, stub) = Client(() => Status(HttpStatusCode.ServiceUnavailable), () => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));

        using var response = await client.GetAsync("https://example.com/catalog.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task It_gives_up_after_three_tries()
    {
        var (client, stub) = Client(() => Status(HttpStatusCode.BadGateway));

        using var response = await client.GetAsync("https://example.com/catalog.json");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(3, stub.Requests.Count);
    }

    [Fact]
    public async Task A_dropped_connection_is_retried()
    {
        var (client, stub) = Client(() => throw new HttpRequestException("Connection reset"), () => StubHttpMessageHandler.Json(HttpStatusCode.OK, "[]"));

        Assert.Equal("[]", await client.GetStringAsync("https://example.com/catalog.json"));
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task A_dropped_connection_still_fails_after_the_last_try()
    {
        var (client, stub) = Client(() => throw new HttpRequestException("Connection reset"));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync("https://example.com/catalog.json"));
        Assert.Equal(3, stub.Requests.Count);
    }

    [Fact]
    public async Task A_body_that_breaks_off_is_downloaded_again()
    {
        var (client, stub) = Client(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent() },
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{\"ok\":true}"));

        Assert.Equal("{\"ok\":true}", await client.GetStringAsync("https://example.com/catalog.json"));
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task A_post_is_never_sent_twice()
    {
        var (client, stub) = Client(() => Status(HttpStatusCode.ServiceUnavailable));

        using var response = await client.PostAsync("https://example.com/repos/o/r/pulls", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(stub.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task An_answer_that_would_not_change_is_not_retried(HttpStatusCode status)
    {
        var (client, stub) = Client(() => Status(status));

        using var response = await client.GetAsync("https://example.com/catalog.json");

        Assert.Equal(status, response.StatusCode);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Too_many_requests_is_retried_only_for_a_short_wait()
    {
        var (soon, soonStub) = Client(() => TooMany(TimeSpan.Zero), () => Status(HttpStatusCode.OK));
        using (var response = await soon.GetAsync("https://example.com/x")) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, soonStub.Requests.Count);

        var (later, laterStub) = Client(() => TooMany(TimeSpan.FromMinutes(5)));
        using (var response = await later.GetAsync("https://example.com/x")) Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(laterStub.Requests);

        var (unsaid, unsaidStub) = Client(() => Status(HttpStatusCode.TooManyRequests));
        using (var response = await unsaid.GetAsync("https://example.com/x")) Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(unsaidStub.Requests);
    }

    [Fact]
    public async Task Cancelling_stops_the_retries()
    {
        using var cts = new CancellationTokenSource();
        var stub = new StubHttpMessageHandler(_ =>
        {
            cts.Cancel();
            return Status(HttpStatusCode.ServiceUnavailable);
        });
        var client = new HttpClient(new HttpRetryHandler(stub, new[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30) }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("https://example.com/x", cts.Token));
        Assert.Single(stub.Requests);
    }

    [Fact]
    public void The_shared_client_keeps_the_timeout_it_was_given() =>
        Assert.Equal(TimeSpan.FromSeconds(20), HttpRetryHandler.CreateClient(TimeSpan.FromSeconds(20)).Timeout);

    private sealed class BrokenContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new IOException("The response ended prematurely.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
