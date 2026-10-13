namespace BasisPM.Core.Services;

public sealed class HttpRetryHandler : DelegatingHandler
{
    private static readonly TimeSpan[] DefaultDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };
    private static readonly TimeSpan LongestRetryAfter = TimeSpan.FromSeconds(10);
    private readonly IReadOnlyList<TimeSpan> _delays;

    public HttpRetryHandler(HttpMessageHandler inner, IReadOnlyList<TimeSpan>? delays = null) : base(inner) => _delays = delays ?? DefaultDelays;

    public static HttpClient CreateClient(TimeSpan timeout) => new(new HttpRetryHandler(new HttpClientHandler())) { Timeout = timeout };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            var last = attempt >= _delays.Count;
            HttpResponseMessage? response = null;
            TimeSpan? wait;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                wait = last ? null : RetryDelay(response, _delays[attempt]);
                if (wait is null)
                {
                    await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
                    return response;
                }
            }
            catch (Exception ex) when (!last && !cancellationToken.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
            {
                wait = _delays[attempt];
            }
            catch
            {
                response?.Dispose();
                throw;
            }
            response?.Dispose();
            await Task.Delay(wait.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan? RetryDelay(HttpResponseMessage response, TimeSpan fallback)
    {
        var status = (int)response.StatusCode;
        if (status is not (408 or 429 or 500 or 502 or 503 or 504)) return null;
        var retryAfter = response.Headers.RetryAfter is { } header ? header.Delta ?? header.Date - DateTimeOffset.UtcNow : null;
        if (retryAfter > LongestRetryAfter || (status == 429 && retryAfter is null)) return null;
        return retryAfter is { } after ? (after < TimeSpan.Zero ? TimeSpan.Zero : after) : fallback;
    }
}
