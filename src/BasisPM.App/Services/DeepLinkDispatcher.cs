namespace BasisPM.App.Services;

/// <summary>
/// Hand-off point between the process entry (which discovers the launch URI and receives
/// forwarded ones) and the running UI. <see cref="Pending"/> holds a URI present at launch;
/// <see cref="UriReceived"/> fires for links forwarded from later launches.
/// </summary>
public static class DeepLinkDispatcher
{
    public static string? Pending { get; set; }

    public static event Action<string>? UriReceived;

    public static void Raise(string uri) => UriReceived?.Invoke(uri);
}
