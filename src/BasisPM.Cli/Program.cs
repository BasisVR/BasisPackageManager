using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        InvariantGlobalization.EnableIfIcuMissing();
        ToolSearchPath.Apply();
        Out.Start();
        try { return await new ConsoleApplication().RunAsync(args); }
        finally { Out.Restore(); }
    }
}
