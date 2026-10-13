using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public static class PackageScanService
{
    public const int MaxFindings = 400;
    private const int MaxFiles = 25000;
    private const int MaxFindingsPerFile = 40;
    private const long MaxSourceBytes = 4L * 1024 * 1024;
    private const long MaxAssemblyBytes = 64L * 1024 * 1024;
    private const long MaxReadBytes = 192L * 1024 * 1024;
    private const int EncodedLength = 2048;
    private const RegexOptions Compiled = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private sealed record Rule(PackageCapability Capability, PackageRisk Risk, Regex Pattern, string? Evidence, string? Requires);

    private static Rule C(PackageCapability capability, PackageRisk risk, string pattern, string? evidence = null, string? requires = null) => new(capability, risk, new Regex(pattern, Compiled), evidence, requires);
    private static Rule S(PackageCapability capability, PackageRisk risk, string pattern, string? evidence = null) => new(capability, risk, new Regex(pattern, Compiled | RegexOptions.IgnoreCase), evidence, null);

    private static readonly Rule[] CodeRules =
    {
        C(PackageCapability.RunsAutomatically, PackageRisk.Caution, @"\[\s*(?:global\s*::\s*)?(?:UnityEditor\s*\.\s*)?InitializeOnLoad(?:Attribute)?\s*[\]\(,]", "[InitializeOnLoad]"),
        C(PackageCapability.RunsAutomatically, PackageRisk.Caution, @"\bInitializeOnLoadMethod(?:Attribute)?\b", "[InitializeOnLoadMethod]"),
        C(PackageCapability.RunsAutomatically, PackageRisk.Caution, @"\bInitializeOnEnterPlayMode(?:Attribute)?\b", "[InitializeOnEnterPlayMode]"),
        C(PackageCapability.RunsAutomatically, PackageRisk.Caution, @"\bDidReloadScripts(?:Attribute)?\b", "[DidReloadScripts]"),
        C(PackageCapability.RunsAutomatically, PackageRisk.Caution, @"\bRuntimeInitializeOnLoadMethod(?:Attribute)?\b", "[RuntimeInitializeOnLoadMethod]"),
        C(PackageCapability.RunsAutomatically, PackageRisk.Caution, @"\bModuleInitializer(?:Attribute)?\b", "[ModuleInitializer]"),
        C(PackageCapability.RunsAutomatically, PackageRisk.Caution, @"\bAssetPostprocessor\b", "AssetPostprocessor"),
        C(PackageCapability.RunsAutomatically, PackageRisk.Caution, @"\bAssetModificationProcessor\b", "AssetModificationProcessor"),
        C(PackageCapability.RunsAutomatically, PackageRisk.Caution, @"\bScriptedImporter\b", "ScriptedImporter"),
        C(PackageCapability.RunsAutomatically, PackageRisk.Caution, @"\b(?:IPreprocessBuild(?:WithReport)?|IPostprocessBuild(?:WithReport)?|IProcessScene(?:WithReport)?|IPreprocessShaders|IPreprocessComputeShaders|IFilterBuildAssemblies|IPostBuildPlayerScriptDLLs|PostProcessBuild(?:Attribute)?|PostProcessScene(?:Attribute)?)\b"),
        C(PackageCapability.StartsPrograms, PackageRisk.High, @"\bProcess\s*\.\s*Start\s*\(", "Process.Start"),
        C(PackageCapability.StartsPrograms, PackageRisk.High, @"\bProcessStartInfo\b", "ProcessStartInfo"),
        C(PackageCapability.StartsPrograms, PackageRisk.High, @"\bSystem\s*\.\s*Diagnostics\s*\.\s*Process\b(?!\w)", "System.Diagnostics.Process"),
        C(PackageCapability.StartsPrograms, PackageRisk.High, @"\bnew\s+Process\s*\(", "new Process()"),
        C(PackageCapability.StartsPrograms, PackageRisk.High, @"\.\s*StartInfo\s*\.\s*(?:FileName|Arguments)\b", "Process.StartInfo"),
        C(PackageCapability.StartsPrograms, PackageRisk.High, @"\b(?:ShellExecute(?:Ex)?[AW]?|CreateProcess(?:AsUser|WithLogon|WithToken)?[AW]?|WinExec|posix_spawnp?|execve|execvp)\b"),
        C(PackageCapability.StartsPrograms, PackageRisk.High, @"\bEditorUtility\s*\.\s*OpenWithDefaultApp\b", "EditorUtility.OpenWithDefaultApp"),
        C(PackageCapability.LoadsHiddenCode, PackageRisk.High, @"\bAssembly\s*\.\s*(?:Load(?!\s*\(\s*\))|LoadFrom|LoadFile|UnsafeLoadFrom|ReflectionOnlyLoad)\s*\(", "Assembly.Load"),
        C(PackageCapability.LoadsHiddenCode, PackageRisk.High, @"\bAppDomain\s*\.\s*CurrentDomain\s*\.\s*Load\s*\(", "AppDomain.Load"),
        C(PackageCapability.LoadsHiddenCode, PackageRisk.High, @"\bAssemblyLoadContext\b", "AssemblyLoadContext"),
        C(PackageCapability.LoadsHiddenCode, PackageRisk.High, @"\bSystem\s*\.\s*Reflection\s*\.\s*Emit\b|\b(?:AssemblyBuilder|ILGenerator|DynamicMethod|TypeBuilder|ModuleBuilder)\b", "Reflection.Emit"),
        C(PackageCapability.LoadsHiddenCode, PackageRisk.High, @"\b(?:CSharpCodeProvider|CodeDomProvider|CompileAssemblyFrom\w+|CSharpCompilation|CSharpScript)\b", "compiles code at run time"),
        C(PackageCapability.LoadsHiddenCode, PackageRisk.High, @"\b(?:GetDelegateForFunctionPointer|VirtualAlloc(?:Ex)?|VirtualProtect(?:Ex)?|CreateRemoteThread(?:Ex)?|WriteProcessMemory|NtCreateThreadEx|QueueUserAPC)\b"),
        C(PackageCapability.LoadsHiddenCode, PackageRisk.High, @"\{\s*(?:0[xX]4[dD]|77)\s*,\s*(?:0[xX]5[aA]|90)\s*,\s*(?:0[xX]90|144|0[xX]0+|0)\s*,", "embedded program bytes"),
        C(PackageCapability.NativeCode, PackageRisk.High, @"\[\s*(?:System\s*\.\s*Runtime\s*\.\s*InteropServices\s*\.\s*)?(?:DllImport|LibraryImport)(?:Attribute)?\b", "[DllImport]"),
        C(PackageCapability.NativeCode, PackageRisk.High, @"\bNativeLibrary\s*\.\s*(?:Load|TryLoad)\b", "NativeLibrary.Load"),
        C(PackageCapability.ReadsLogins, PackageRisk.High, @"\bProtectedData\s*\.\s*Unprotect\b|\bCryptUnprotectData\b", "decrypts saved passwords (DPAPI)"),
        C(PackageCapability.ReadsLogins, PackageRisk.High, @"\bCred(?:Read|Enumerate)[AW]?\b", "Windows Credential Manager"),
        C(PackageCapability.SystemSettings, PackageRisk.High, @"\bMicrosoft\s*\.\s*Win32\s*\.\s*Registry\b|\bRegistry\s*\.\s*(?:CurrentUser|LocalMachine|ClassesRoot|Users|CurrentConfig|SetValue|GetValue)\b|\bRegistryKey\b|\bReg(?:OpenKey|QueryValue|SetValue|GetValue|CreateKey)(?:Ex)?[AW]?\b", "Windows registry"),
        C(PackageCapability.Network, PackageRisk.Caution, @"\bUnityWebRequest\w*\b", "UnityWebRequest"),
        C(PackageCapability.Network, PackageRisk.Caution, @"\bHttpClient\b", "HttpClient"),
        C(PackageCapability.Network, PackageRisk.Caution, @"\bWebClient\b", "WebClient"),
        C(PackageCapability.Network, PackageRisk.Caution, @"\b(?:Http|Ftp|File)?WebRequest\s*\.\s*Create\w*\s*\(|\b(?:HttpWebRequest|FtpWebRequest)\b", "WebRequest"),
        C(PackageCapability.Network, PackageRisk.Caution, @"\b(?:TcpClient|TcpListener|UdpClient|ClientWebSocket|SmtpClient|HttpListener)\b"),
        C(PackageCapability.Network, PackageRisk.Caution, @"\bnew\s+(?:System\s*\.\s*Net\s*\.\s*Sockets\s*\.\s*)?Socket\s*\(", "Socket"),
        C(PackageCapability.Network, PackageRisk.Caution, @"\bnew\s+WWW\s*\(", "WWW"),
        C(PackageCapability.Network, PackageRisk.Caution, @"\bDns\s*\.\s*(?:GetHost\w*|Resolve)\b", "Dns"),
        C(PackageCapability.AddsPackages, PackageRisk.Caution, @"\bPackageManager\s*\.\s*Client\s*\.\s*(?:Add|AddAndRemove|Embed)\s*\(", "PackageManager.Client.Add"),
        C(PackageCapability.AddsPackages, PackageRisk.Caution, @"\bClient\s*\.\s*(?:Add|AddAndRemove|Embed)\s*\(", "PackageManager.Client.Add", requires: "PackageManager"),
    };

    private static readonly Rule[] StringRules =
    {
        S(PackageCapability.ReadsLogins, PackageRisk.High, @"discord(?:canary|ptb|development)?[\\/]+Local Storage|Local Storage[\\/]+leveldb", "Discord login storage"),
        S(PackageCapability.ReadsLogins, PackageRisk.High, @"(?:Google[\\/]+Chrome|Microsoft[\\/]+Edge|BraveSoftware|Opera Software|Vivaldi|Yandex[\\/]+YandexBrowser|Chromium)[\\/]+(?:User Data|Default)", "browser passwords or cookies"),
        new(PackageCapability.ReadsLogins, PackageRisk.High, new Regex(@"(?:^|[\\/])(?:Login Data|Local State|Web Data|Network[\\/]+Cookies)(?:$|[\\/])", Compiled), "browser passwords or cookies", null),
        S(PackageCapability.ReadsLogins, PackageRisk.High, @"Mozilla[\\/]+Firefox[\\/]+Profiles|\bkey[34]\.db\b|\blogins\.json\b|\bcookies\.sqlite\b", "browser passwords or cookies"),
        S(PackageCapability.ReadsLogins, PackageRisk.High, @"[\\/]\.ssh\b|\bid_(?:rsa|ed25519|ecdsa|dsa)\b|\.git-credentials\b|\.netrc\b|\.aws[\\/]+credentials", "SSH keys or saved credentials"),
        S(PackageCapability.ReadsLogins, PackageRisk.High, @"\bwallet\.dat\b|\bExodus[\\/]|\bElectrum[\\/]|\bMetaMask\b|nkbihfbeogaeaoehlefnkodbefgpgknn|\bAtomic[\\/]+Local Storage", "crypto wallets"),
        S(PackageCapability.ReadsLogins, PackageRisk.High, @"Telegram Desktop|[\\/]tdata\b", "Telegram session"),
        S(PackageCapability.ReadsLogins, PackageRisk.High, @"loginusers\.vdf|\bssfn\d*\b|config[\\/]+config\.vdf", "Steam login"),
        S(PackageCapability.Network, PackageRisk.High, @"discord(?:app)?\.com[\\/]+api[\\/]+webhooks|\bapi\.telegram\.org[\\/]+bot", "sends data to a chat webhook"),
        S(PackageCapability.StartsPrograms, PackageRisk.High, @"\bcmd(?:\.exe)?\s+/[ck]\b|\bpowershell(?:\.exe)?\b|\bpwsh(?:\.exe)?\b|/bin/(?:ba|z|da)?sh\b|\bmshta(?:\.exe)?\b|\brundll32(?:\.exe)?\b|\bregsvr32(?:\.exe)?\b|\bcertutil(?:\.exe)?\b|\bbitsadmin(?:\.exe)?\b|\bwscript(?:\.exe)?\b|\bcscript(?:\.exe)?\b|\bschtasks(?:\.exe)?\b|\bcurl\s+-|\bwget\s+https?:"),
        S(PackageCapability.StartsPrograms, PackageRisk.High, @"System\.Diagnostics\.Process\b", "System.Diagnostics.Process (by name)"),
        S(PackageCapability.LoadsHiddenCode, PackageRisk.High, @"System\.Reflection\.Assembly\b|System\.Runtime\.Loader\b", "loads code by name"),
    };

    private static readonly Dictionary<string, (PackageCapability Capability, PackageRisk Risk)> AssemblyTypes = new(StringComparer.Ordinal)
    {
        ["System.Diagnostics.Process"] = (PackageCapability.StartsPrograms, PackageRisk.High),
        ["System.Diagnostics.ProcessStartInfo"] = (PackageCapability.StartsPrograms, PackageRisk.High),
        ["System.Reflection.Emit.AssemblyBuilder"] = (PackageCapability.LoadsHiddenCode, PackageRisk.High),
        ["System.Reflection.Emit.DynamicMethod"] = (PackageCapability.LoadsHiddenCode, PackageRisk.High),
        ["System.Reflection.Emit.ILGenerator"] = (PackageCapability.LoadsHiddenCode, PackageRisk.High),
        ["System.Runtime.Loader.AssemblyLoadContext"] = (PackageCapability.LoadsHiddenCode, PackageRisk.High),
        ["Microsoft.CSharp.CSharpCodeProvider"] = (PackageCapability.LoadsHiddenCode, PackageRisk.High),
        ["System.Security.Cryptography.ProtectedData"] = (PackageCapability.ReadsLogins, PackageRisk.High),
        ["Microsoft.Win32.Registry"] = (PackageCapability.SystemSettings, PackageRisk.High),
        ["Microsoft.Win32.RegistryKey"] = (PackageCapability.SystemSettings, PackageRisk.High),
        ["UnityEditor.InitializeOnLoadAttribute"] = (PackageCapability.RunsAutomatically, PackageRisk.Caution),
        ["UnityEditor.InitializeOnLoadMethodAttribute"] = (PackageCapability.RunsAutomatically, PackageRisk.Caution),
        ["UnityEditor.Callbacks.DidReloadScripts"] = (PackageCapability.RunsAutomatically, PackageRisk.Caution),
        ["UnityEditor.AssetPostprocessor"] = (PackageCapability.RunsAutomatically, PackageRisk.Caution),
        ["UnityEngine.RuntimeInitializeOnLoadMethodAttribute"] = (PackageCapability.RunsAutomatically, PackageRisk.Caution),
        ["System.Runtime.CompilerServices.ModuleInitializerAttribute"] = (PackageCapability.RunsAutomatically, PackageRisk.Caution),
        ["UnityEngine.Networking.UnityWebRequest"] = (PackageCapability.Network, PackageRisk.Caution),
        ["System.Net.WebClient"] = (PackageCapability.Network, PackageRisk.Caution),
        ["System.Net.Http.HttpClient"] = (PackageCapability.Network, PackageRisk.Caution),
        ["System.Net.WebRequest"] = (PackageCapability.Network, PackageRisk.Caution),
        ["System.Net.HttpWebRequest"] = (PackageCapability.Network, PackageRisk.Caution),
        ["System.Net.Sockets.Socket"] = (PackageCapability.Network, PackageRisk.Caution),
        ["System.Net.Sockets.TcpClient"] = (PackageCapability.Network, PackageRisk.Caution),
        ["System.Net.Sockets.UdpClient"] = (PackageCapability.Network, PackageRisk.Caution),
        ["UnityEditor.PackageManager.Client"] = (PackageCapability.AddsPackages, PackageRisk.Caution),
    };

    private static readonly Regex LoadMember = new(@"^(?:Load|LoadFrom|LoadFile|UnsafeLoadFrom|ReflectionOnlyLoad)$", Compiled);
    private static readonly Regex StartsProgramImport = new(@"^(?:ShellExecute(?:Ex)?[AW]?|CreateProcess\w*|WinExec|system|popen|execv\w*|posix_spawnp?)$", Compiled);
    private static readonly Regex HiddenCodeImport = new(@"^(?:VirtualAlloc(?:Ex)?|VirtualProtect(?:Ex)?|CreateRemoteThread(?:Ex)?|WriteProcessMemory|NtCreateThreadEx|QueueUserAPC|LoadLibrary\w*|GetProcAddress|dlopen|dlsym)$", Compiled);
    private static readonly Regex LoginImport = new(@"^(?:CryptUnprotectData|Cred(?:Read|Enumerate)[AW]?)$", Compiled);
    private static readonly Regex RegistryImport = new(@"^Reg\w+$", Compiled);
    private static readonly Regex UnicodeEscape = new(@"\\u([0-9A-Fa-f]{4})|\\U([0-9A-Fa-f]{8})", Compiled);
    private static readonly Regex AnalyzerOption = new(@"^[-/](?:a|analyzer|generator):", Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ReferenceOption = new(@"^[-/](?:r|reference|addmodule):", Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> NativeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".so", ".dylib", ".a", ".lib", ".o", ".bundle", ".framework", ".xcframework" };
    private static readonly HashSet<string> CompiledExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jar", ".aar", ".winmd", ".class" };
    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".msi", ".msix", ".appx", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".jse", ".wsf", ".wsh", ".hta",
        ".sh", ".bash", ".zsh", ".command", ".app", ".lnk", ".reg", ".cpl", ".pif", ".appimage", ".run", ".deb", ".rpm", ".pkg", ".dmg",
    };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".meta", ".json", ".asmdef", ".asmref", ".rsp", ".md", ".txt", ".xml", ".yaml", ".yml", ".csv", ".tsv", ".ini", ".cfg", ".editorconfig",
        ".shader", ".hlsl", ".cginc", ".compute", ".glsl", ".shadergraph", ".shadersubgraph", ".shadervariants", ".raytrace", ".uss", ".uxml", ".tss",
        ".mat", ".asset", ".prefab", ".unity", ".anim", ".controller", ".overridecontroller", ".mask", ".physicmaterial", ".physicsmaterial2d",
        ".lighting", ".signal", ".playable", ".guiskin", ".fontsettings", ".terrainlayer", ".brush", ".spriteatlas", ".spriteatlasv2", ".preset",
        ".mixer", ".rendertexture", ".cubemap", ".flare", ".giparams", ".inputactions", ".vfx", ".vfxoperator", ".vfxblock", ".html", ".htm", ".css",
        ".js", ".jslib", ".jspre", ".ts", ".py", ".props", ".targets", ".csproj", ".sln", ".slnx", ".svg", ".license", ".gitignore", ".gitattributes",
        ".c", ".cc", ".cpp", ".h", ".hpp", ".m", ".mm", ".java", ".kt", ".gradle", ".pro", ".plist", ".xib", ".storyboard", ".strings", ".swift",
    };

    public static Task<PackageScan> ScanAsync(string packageRoot, CancellationToken ct = default) => Task.Run(() => Scan(packageRoot, ct), ct);

    public static PackageScan Scan(string packageRoot, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(packageRoot) || !Directory.Exists(packageRoot)) return PackageScan.Empty;
        var scanner = new Scanner(Path.GetFullPath(packageRoot), ct);
        scanner.Run();
        return new PackageScan(scanner.Findings, scanner.Scripts, scanner.Binaries, scanner.Files, scanner.Incomplete);
    }

    public static string ShortCommit(string? commit) => commit is { Length: > 10 } ? commit[..10] : commit ?? "";

    public static bool SameCommit(string? a, string? b) =>
        a is { Length: >= 7 } && b is { Length: >= 7 } && (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase));

    private sealed class Scanner(string root, CancellationToken ct)
    {
        public readonly List<PackageFinding> Findings = new();
        public int Scripts, Binaries, Files;
        public bool Incomplete;
        private long _read;
        private readonly HashSet<(string, PackageCapability, string, int)> _seen = new();
        private readonly Dictionary<string, int> _perFile = new(StringComparer.Ordinal);

        public void Run()
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var folder = pending.Pop();
                foreach (var dir in Entries(folder, directories: true))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith('.') || IsLink(dir)) continue;
                    var ext = Path.GetExtension(name);
                    if (NativeExtensions.Contains(ext)) { Binaries++; Add(PackageCapability.NativeCode, PackageRisk.High, dir, 0, $"native plugin ({ext})"); continue; }
                    if (ProgramExtensions.Contains(ext)) { Binaries++; Add(PackageCapability.Programs, PackageRisk.High, dir, 0, $"program ({ext})"); continue; }
                    pending.Push(dir);
                }
                foreach (var file in Entries(folder, directories: false))
                {
                    if (IsLink(file)) continue;
                    if (++Files > MaxFiles || Findings.Count >= MaxFindings) { Incomplete = true; return; }
                    try { Inspect(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Scanning {file}", ex); }
                }
            }
        }

        private static IEnumerable<string> Entries(string folder, bool directories)
        {
            try { return (directories ? Directory.EnumerateDirectories(folder) : Directory.EnumerateFiles(folder)).OrderBy(p => p, StringComparer.Ordinal).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Listing {folder} for the package scan", ex); return Array.Empty<string>(); }
        }

        private static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        }

        private void Inspect(string file)
        {
            var name = Path.GetFileName(file);
            var ext = Path.GetExtension(name);
            if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)) { Scripts++; ScanSource(file); return; }
            if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) { Binaries++; ScanLibrary(file); return; }
            if (ext.Equals(".rsp", StringComparison.OrdinalIgnoreCase)) { ScanResponseFile(file); return; }
            if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase)) { ScanPackageJson(file); return; }
            if (NativeExtensions.Contains(ext)) { Binaries++; Add(PackageCapability.NativeCode, PackageRisk.High, file, 0, $"native library ({ext})"); return; }
            if (CompiledExtensions.Contains(ext)) { Binaries++; Add(PackageCapability.CompiledCode, PackageRisk.High, file, 0, $"compiled code ({ext})"); return; }
            if (ProgramExtensions.Contains(ext)) { Binaries++; Add(PackageCapability.Programs, PackageRisk.High, file, 0, $"program ({ext})"); return; }
            if (TextExtensions.Contains(ext)) return;
            if (Sniff(file) is { } kind) { Binaries++; Add(PackageCapability.Programs, PackageRisk.High, file, 0, $"{kind} saved as {(ext.Length > 0 ? ext : "a file with no extension")}"); }
        }

        private static string? Sniff(string file)
        {
            Span<byte> head = stackalloc byte[4];
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            if (stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) < 4) return null;
            if (head[0] == 0x4D && head[1] == 0x5A) return "Windows program";
            if (head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F') return "Linux program";
            var magic = (uint)(head[0] << 24 | head[1] << 16 | head[2] << 8 | head[3]);
            return magic is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE ? "macOS program" : null;
        }

        private void ScanSource(string file)
        {
            var length = new FileInfo(file).Length;
            if (length > MaxSourceBytes || _read + length > MaxReadBytes) { Incomplete = true; return; }
            _read += length;
            var lexed = CSharpLexer.Split(File.ReadAllText(file));
            var code = UnicodeEscape.Replace(lexed.Code, m => char.ConvertFromUtf32(Convert.ToInt32(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, 16)));
            var lineStarts = new List<int> { 0 };
            for (var i = 0; i < code.Length; i++) if (code[i] == '\n') lineStarts.Add(i + 1);
            foreach (var rule in CodeRules)
            {
                if (rule.Requires is { } needed && !code.Contains(needed, StringComparison.Ordinal)) continue;
                var match = rule.Pattern.Match(code);
                for (var hits = 0; match.Success && hits < 8; hits++, match = match.NextMatch())
                {
                    var index = lineStarts.BinarySearch(match.Index);
                    var line = (index >= 0 ? index : ~index - 1) + 1;
                    if (rule.Evidence == "[DllImport]" && lexed.Strings.FirstOrDefault(s => s.Line == line).Text is { Length: > 0 } library)
                        Add(rule.Capability, library == "__Internal" ? PackageRisk.Caution : rule.Risk, file, line, $"[DllImport(\"{Collapse(library)}\")]");
                    else Add(rule.Capability, rule.Risk, file, line, rule.Evidence ?? Collapse(match.Value));
                }
            }
            foreach (var (line, text) in lexed.Strings) ScanText(file, line, text);
        }

        private void ScanText(string file, int line, string text)
        {
            foreach (var rule in StringRules)
                if (rule.Pattern.Match(text) is { Success: true } match)
                    Add(rule.Capability, rule.Risk, file, line, rule.Evidence ?? Collapse(match.Value));
            if (IsEncoded(text)) Add(PackageCapability.EncodedData, PackageRisk.Caution, file, line, $"{text.Length / 1024 + 1} KB of encoded text");
        }

        private static bool IsEncoded(string text)
        {
            if (text.Length < EncodedLength) return false;
            var spaces = 0;
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c)) spaces++;
                else if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '/' or '=' or '-' or '_' or ',')) return false;
            }
            return spaces * 20 < text.Length;
        }

        private void ScanResponseFile(string file)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var option = lines[i].Trim();
                if (AnalyzerOption.IsMatch(option)) Add(PackageCapability.RunsInCompiler, PackageRisk.High, file, i + 1, Collapse(option));
                else if (ReferenceOption.IsMatch(option)) Add(PackageCapability.CompiledCode, PackageRisk.High, file, i + 1, Collapse(option));
            }
        }

        private void ScanPackageJson(string file)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("basisServer", out var server) || server.ValueKind != JsonValueKind.Object) return;
                if (!server.TryGetProperty("nuget", out var nuget) || nuget.ValueKind != JsonValueKind.Object) return;
                foreach (var package in nuget.EnumerateObject())
                    Add(PackageCapability.CompiledCode, PackageRisk.Caution, file, 0, $"NuGet package {package.Name} {(package.Value.ValueKind == JsonValueKind.String ? package.Value.GetString() : "")}".TrimEnd() + " (Basis Server)");
            }
            catch (JsonException) { }
        }

        private void ScanLibrary(string file)
        {
            if (IsAnalyzer(file)) Add(PackageCapability.RunsInCompiler, PackageRisk.High, file, 0, "RoslynAnalyzer label");
            var length = new FileInfo(file).Length;
            if (length > MaxAssemblyBytes) { Add(PackageCapability.CompiledCode, PackageRisk.High, file, 0, "compiled library"); return; }
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata)
                {
                    Add(PackageCapability.NativeCode, PackageRisk.High, file, 0, "native library (.dll)");
                    return;
                }
                Add(PackageCapability.CompiledCode, PackageRisk.High, file, 0, "compiled .NET library");
                ScanMetadata(file, pe.GetMetadataReader());
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
            {
                Add(PackageCapability.NativeCode, PackageRisk.High, file, 0, "library in an unknown format (.dll)");
            }
        }

        private static bool IsAnalyzer(string file)
        {
            var meta = file + ".meta";
            if (!File.Exists(meta)) return false;
            try { return File.ReadLines(meta).Any(l => l.Trim() is "- RoslynAnalyzer" or "- SourceGenerator"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        private void ScanMetadata(string file, MetadataReader reader)
        {
            foreach (var handle in reader.TypeReferences)
            {
                var type = reader.GetTypeReference(handle);
                var name = FullName(reader.GetString(type.Namespace), reader.GetString(type.Name));
                if (AssemblyTypes.TryGetValue(name, out var hit)) Add(hit.Capability, hit.Risk, file, 0, name);
            }
            foreach (var handle in reader.MemberReferences)
            {
                var member = reader.GetMemberReference(handle);
                if (member.Parent.Kind != HandleKind.TypeReference) continue;
                var parent = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
                var parentName = FullName(reader.GetString(parent.Namespace), reader.GetString(parent.Name));
                var memberName = reader.GetString(member.Name);
                if (parentName is "System.Reflection.Assembly" or "System.AppDomain" && LoadMember.IsMatch(memberName) && !LoadsByName(member, memberName))
                    Add(PackageCapability.LoadsHiddenCode, PackageRisk.High, file, 0, $"{parentName}.{memberName}");
                else if (parentName == "System.Runtime.InteropServices.Marshal" && memberName == "GetDelegateForFunctionPointer")
                    Add(PackageCapability.LoadsHiddenCode, PackageRisk.High, file, 0, $"{parentName}.{memberName}");
            }
            foreach (var handle in reader.MethodDefinitions)
            {
                var method = reader.GetMethodDefinition(handle);
                var import = method.GetImport();
                if (import.Module.IsNil) continue;
                var module = reader.GetString(reader.GetModuleReference(import.Module).Name);
                var entry = import.Name.IsNil ? reader.GetString(method.Name) : reader.GetString(import.Name);
                var evidence = $"{module}!{entry}";
                if (StartsProgramImport.IsMatch(entry)) Add(PackageCapability.StartsPrograms, PackageRisk.High, file, 0, evidence);
                else if (HiddenCodeImport.IsMatch(entry)) Add(PackageCapability.LoadsHiddenCode, PackageRisk.High, file, 0, evidence);
                else if (LoginImport.IsMatch(entry)) Add(PackageCapability.ReadsLogins, PackageRisk.High, file, 0, evidence);
                else if (RegistryImport.IsMatch(entry) && module.StartsWith("advapi32", StringComparison.OrdinalIgnoreCase)) Add(PackageCapability.SystemSettings, PackageRisk.High, file, 0, evidence);
                else Add(PackageCapability.NativeCode, module == "__Internal" ? PackageRisk.Caution : PackageRisk.High, file, 0, $"calls into {module}");
            }
            if (reader.GetHeapSize(HeapIndex.UserString) <= 1) return;
            var text = MetadataTokens.UserStringHandle(1);
            for (var i = 0; i < 200000 && !text.IsNil; i++)
            {
                var value = reader.GetUserString(text);
                if (value.Length > 0) ScanText(file, 0, value);
                text = reader.GetNextHandle(text);
            }
        }

        private static string FullName(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;

        private static bool LoadsByName(MemberReference member, string name)
        {
            if (name != "Load" || member.GetKind() != MemberReferenceKind.Method) return false;
            try { return member.DecodeMethodSignature(TypeNames.Instance, null).ParameterTypes is [nameof(PrimitiveTypeCode.String)]; }
            catch (BadImageFormatException) { return false; }
        }

        private sealed class TypeNames : ISignatureTypeProvider<string, object?>
        {
            public static readonly TypeNames Instance = new();
            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => "type";
            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => "type";
            public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => "type";
            public string GetSZArrayType(string elementType) => elementType + "[]";
            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[*]";
            public string GetByReferenceType(string elementType) => elementType + "&";
            public string GetPointerType(string elementType) => elementType + "*";
            public string GetPinnedType(string elementType) => elementType;
            public string GetGenericInstantiation(string genericType, System.Collections.Immutable.ImmutableArray<string> typeArguments) => genericType;
            public string GetGenericMethodParameter(object? genericContext, int index) => "T";
            public string GetGenericTypeParameter(object? genericContext, int index) => "T";
            public string GetFunctionPointerType(MethodSignature<string> signature) => "fn";
            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        }

        private void Add(PackageCapability capability, PackageRisk risk, string path, int line, string evidence)
        {
            if (Findings.Count >= MaxFindings) { Incomplete = true; return; }
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!_seen.Add((relative, capability, evidence, line))) return;
            var count = _perFile.GetValueOrDefault(relative);
            if (count >= MaxFindingsPerFile) return;
            _perFile[relative] = count + 1;
            Findings.Add(new PackageFinding(capability, risk, relative, line, evidence));
        }

        private static string Collapse(string value)
        {
            var collapsed = Regex.Replace(value, @"\s+", " ").Trim();
            return collapsed.Length > 80 ? collapsed[..80] + "…" : collapsed;
        }
    }
}

public static class CSharpLexer
{
    public sealed record Result(string Code, IReadOnlyList<(int Line, string Text)> Strings);

    public static Result Split(string source)
    {
        var lexer = new State(source);
        lexer.Code(hole: 0);
        return new Result(lexer.Output.ToString(), lexer.Strings);
    }

    private sealed class State(string s)
    {
        public readonly StringBuilder Output = new(s.Length);
        public readonly List<(int Line, string Text)> Strings = new();
        private int _i, _line = 1;
        private bool _lineStart = true;

        private char At(int offset) => _i + offset < s.Length ? s[_i + offset] : '\0';

        private void NewLine()
        {
            _line++;
            Output.Append('\n');
            _lineStart = true;
        }

        public void Code(int hole)
        {
            var depth = 0;
            while (_i < s.Length)
            {
                var c = s[_i];
                if (c == '\n') { _i++; NewLine(); continue; }
                if (c == '#' && _lineStart && hole == 0) { SkipToLineEnd(); continue; }
                if (c is not (' ' or '\t' or '\r')) _lineStart = false;
                if (c == '/' && At(1) == '/') { SkipToLineEnd(); continue; }
                if (c == '/' && At(1) == '*') { BlockComment(); continue; }
                if (c == '\'') { CharLiteral(); continue; }
                if (StringStart() is { } start) { StringLiteral(start); continue; }
                if (hole > 0)
                {
                    if (c == '{') depth++;
                    else if (c == '}')
                    {
                        if (depth == 0) return;
                        depth--;
                    }
                }
                Output.Append(c);
                _i++;
            }
        }

        private void SkipToLineEnd()
        {
            while (_i < s.Length && s[_i] != '\n') _i++;
            Output.Append(' ');
        }

        private void BlockComment()
        {
            _i += 2;
            while (_i < s.Length && !(s[_i] == '*' && At(1) == '/'))
            {
                if (s[_i] == '\n') { _line++; Output.Append('\n'); }
                _i++;
            }
            _i = Math.Min(s.Length, _i + 2);
            Output.Append(' ');
        }

        private void CharLiteral()
        {
            var end = _i + 1;
            if (end < s.Length && s[end] == '\\') end += 2;
            else end++;
            while (end < s.Length && end - _i < 12 && s[end] != '\'' && s[end] != '\n') end++;
            _i = end < s.Length && s[end] == '\'' ? end + 1 : _i + 1;
            Output.Append(' ');
        }

        private (int Dollars, bool Verbatim, int Quotes)? StringStart()
        {
            var j = _i;
            var dollars = 0;
            var verbatim = false;
            while (j < s.Length && (s[j] == '$' || s[j] == '@'))
            {
                if (s[j] == '$') dollars++;
                else if (verbatim) return null;
                else verbatim = true;
                j++;
            }
            if (j >= s.Length || s[j] != '"') return null;
            if (dollars == 0 && !verbatim && j != _i) return null;
            var quotes = 0;
            while (j + quotes < s.Length && s[j + quotes] == '"') quotes++;
            if (quotes == 2 && !verbatim) quotes = 1;
            if (quotes >= 3 && !verbatim) return (dollars, false, quotes);
            return (dollars, verbatim, 1);
        }

        private void StringLiteral((int Dollars, bool Verbatim, int Quotes) start)
        {
            var startLine = _line;
            while (s[_i] != '"') _i++;
            _i += start.Quotes;
            var text = new StringBuilder();
            var raw = start.Quotes >= 3;
            while (_i < s.Length)
            {
                var c = s[_i];
                if (raw)
                {
                    if (c == '"' && Run('"') >= start.Quotes) { _i += start.Quotes; break; }
                }
                else if (c == '"')
                {
                    if (start.Verbatim && At(1) == '"') { text.Append('"'); _i += 2; continue; }
                    _i++;
                    break;
                }
                else if (c == '\n' && !start.Verbatim) break;
                if (start.Dollars > 0 && c == '{')
                {
                    var braces = Math.Max(1, raw ? start.Dollars : 1);
                    if (!raw && At(1) == '{') { text.Append('{'); _i += 2; continue; }
                    if (Run('{') >= braces)
                    {
                        _i += braces;
                        Output.Append(' ');
                        Code(hole: 1);
                        _i = Math.Min(s.Length, _i + braces);
                        continue;
                    }
                }
                if (start.Dollars > 0 && c == '}' && !raw && At(1) == '}') { text.Append('}'); _i += 2; continue; }
                if (c == '\\' && !start.Verbatim && !raw) { _i += Escape(text); continue; }
                if (c == '\n') { _line++; Output.Append('\n'); }
                text.Append(c);
                _i++;
            }
            Output.Append(' ');
            _lineStart = false;
            if (text.Length > 0) Strings.Add((startLine, text.ToString()));
        }

        private int Run(char c)
        {
            var n = 0;
            while (_i + n < s.Length && s[_i + n] == c) n++;
            return n;
        }

        private int Escape(StringBuilder text)
        {
            var next = At(1);
            switch (next)
            {
                case 'u' when _i + 6 <= s.Length && int.TryParse(s.AsSpan(_i + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out var u):
                    text.Append((char)u);
                    return 6;
                case 'U' when _i + 10 <= s.Length && int.TryParse(s.AsSpan(_i + 2, 8), System.Globalization.NumberStyles.HexNumber, null, out var big) && big is >= 0 and <= 0x10FFFF:
                    text.Append(char.ConvertFromUtf32(big));
                    return 10;
                case 'x':
                    var digits = 0;
                    while (digits < 4 && _i + 2 + digits < s.Length && Uri.IsHexDigit(s[_i + 2 + digits])) digits++;
                    if (digits > 0 && int.TryParse(s.AsSpan(_i + 2, digits), System.Globalization.NumberStyles.HexNumber, null, out var x)) text.Append((char)x);
                    return 2 + digits;
                case 'n': text.Append('\n'); return 2;
                case 't': text.Append('\t'); return 2;
                case 'r': text.Append('\r'); return 2;
                case '0': text.Append('\0'); return 2;
                case '\0': return 1;
                case '\n': return 1;
                default: text.Append(next); return 2;
            }
        }
    }
}
