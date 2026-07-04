using Microsoft.AspNetCore.Mvc.Testing;

namespace BasisPM.Server.Tests.Infrastructure;

public abstract class RegistryFactory : WebApplicationFactory<Program>
{
    private readonly byte[]? _originalRegistry;

    public string RegistryPath { get; } = TestServerRoot.RegistryDataPath;

    protected RegistryFactory(bool enableSubmissions, string? submitToken)
    {
        _originalRegistry = File.Exists(RegistryPath) ? File.ReadAllBytes(RegistryPath) : null;
        TryDelete(RegistryPath);

        Environment.SetEnvironmentVariable("BASISPM_ENABLE_SUBMISSIONS", enableSubmissions ? "1" : null);
        Environment.SetEnvironmentVariable("BASISPM_SUBMIT_TOKEN", submitToken);

        _ = Server;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        Environment.SetEnvironmentVariable("BASISPM_ENABLE_SUBMISSIONS", null);
        Environment.SetEnvironmentVariable("BASISPM_SUBMIT_TOKEN", null);
        try
        {
            if (_originalRegistry is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);
                File.WriteAllBytes(RegistryPath, _originalRegistry);
            }
            else
            {
                TryDelete(RegistryPath);
            }
        }
        catch {  }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

public sealed class DisabledSubmissionsFactory : RegistryFactory
{
    public DisabledSubmissionsFactory() : base(enableSubmissions: false, submitToken: null) { }
}

public sealed class OpenSubmissionsFactory : RegistryFactory
{
    public OpenSubmissionsFactory() : base(enableSubmissions: true, submitToken: null) { }
}

public sealed class TokenSubmissionsFactory : RegistryFactory
{
    public const string Token = "s3cr3t-test-token";
    public TokenSubmissionsFactory() : base(enableSubmissions: false, submitToken: Token) { }
}
