using System.Net;
using System.Text;

namespace BasisPM.Core.Tests.TestSupport;

public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public List<HttpRequestMessage> Requests { get; } = new();

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
            await request.Content.LoadIntoBufferAsync();
        Requests.Add(request);
        return _responder(request);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public static HttpClient Always(string json, HttpStatusCode status = HttpStatusCode.OK, Uri? baseAddress = null)
    {
        var client = new HttpClient(new StubHttpMessageHandler(_ => Json(status, json)));
        if (baseAddress is not null) client.BaseAddress = baseAddress;
        return client;
    }

    public static HttpClient AlwaysThrows()
        => new(new StubHttpMessageHandler(_ => throw new HttpRequestException("network down")));

    public static HttpClient AlwaysStatus(HttpStatusCode status)
        => new(new StubHttpMessageHandler(_ => new HttpResponseMessage(status)));

    public static HttpClient Route(IEnumerable<(string UrlContains, HttpStatusCode Status, string Json)> routes, Uri? baseAddress = null)
    {
        var list = routes.ToList();
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            foreach (var (contains, status, json) in list)
                if (url.Contains(contains, StringComparison.OrdinalIgnoreCase))
                    return Json(status, json);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = new HttpClient(handler);
        if (baseAddress is not null) client.BaseAddress = baseAddress;
        return client;
    }
}
