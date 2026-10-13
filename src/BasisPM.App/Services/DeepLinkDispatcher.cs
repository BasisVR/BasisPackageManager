namespace BasisPM.App.Services;

/// <summary>
/// Hand-off point between the process entry (which discovers the launch URI and receives
/// forwarded ones) and the running UI. <see cref="Pending"/> holds a URI present at launch;
/// <see cref="UriReceived"/> fires for links forwarded from later launches.
/// </summary>
public static class DeepLinkDispatcher
{
    private static readonly object Gate = new();
    private static readonly List<string> Queued = new();
    private static bool _ready;

    public static event Action<string>? UriReceived;

    public static void Raise(string uri) => UriReceived?.Invoke(uri);

    public static void Deliver(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return;
        lock (Gate)
        {
            if (!_ready)
            {
                if (Queued.Count < 8) Queued.Add(uri);
                return;
            }
        }
        Raise(uri);
    }

    public static IReadOnlyList<string> TakePending()
    {
        lock (Gate)
        {
            _ready = true;
            var pending = Queued.ToList();
            Queued.Clear();
            return pending;
        }
    }
}
