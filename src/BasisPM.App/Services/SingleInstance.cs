using System.IO.Pipes;

namespace BasisPM.App.Services;

/// <summary>
/// Keeps one running instance and forwards deep links from later launches to it over a
/// named pipe (so clicking a <c>basispm://</c> link focuses the existing window instead of
/// opening a second one). All operations are best-effort — failures fall back to a normal launch.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\BasisPackageManager.SingleInstance";
    private const string PipeName = "BasisPackageManager.DeepLink";

    private static Mutex? _mutex;

    /// <summary>True if this process is the first/primary instance; false if another is already running.</summary>
    public static bool TryBecomePrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        return createdNew;
    }

    public static void ForwardToPrimary(string uri)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(uri);
        }
        catch { }
    }

    public static void StartServer(Action<string> onUri)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    // Bounded read (a same-user process could write to this pipe): cap the size and only
                    // ever forward a well-formed basispm:// deep link — never an arbitrary string.
                    var buf = new char[4096];
                    var n = reader.Read(buf, 0, buf.Length);
                    if (n > 0)
                    {
                        var line = new string(buf, 0, n).Split('\n', 2)[0].Trim();
                        if (DeepLink.IsDeepLink(line)) onUri(line);
                    }
                }
                catch { Thread.Sleep(200); }
            }
        })
        {
            IsBackground = true,
            Name = "DeepLinkPipeServer",
        };
        thread.Start();
    }
}
