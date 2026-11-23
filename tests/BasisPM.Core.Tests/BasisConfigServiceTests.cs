using System.Text;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class BasisConfigServiceTests
{
    private const string Settings = "%YAML 1.1\nPlayerSettings:\n  companyName: Basis Unity\n  productName: Basis Unity\n  bundleVersion: 2.0.1\n  applicationIdentifier:\n    Android: com.BasisUnity.BasisUnity\n    Standalone: com.Basis-Unity.Basis-Unity\n    iPhone: com.Basis-Unity.Basis-Unity\n  buildNumber:\n    Standalone: 0\n  overrideDefaultApplicationIdentifier: 0\n  AndroidBundleVersionCode: 221\n";

    private static BasisConfig MyApp => new() { ProductName = "MyApp", CompanyName = "MyApp", Identifier = "com.myapp.app", Version = "1.2.0" };

    [Fact]
    public void ReadsIdentityFromProjectSettings()
    {
        var identity = BasisConfigService.ReadIdentity(Settings);
        Assert.Equal("Basis Unity", identity.ProductName);
        Assert.Equal("Basis Unity", identity.CompanyName);
        Assert.Equal("2.0.1", identity.Version);
        Assert.Equal("com.BasisUnity.BasisUnity", identity.Identifiers["Android"]);
        Assert.Equal(3, identity.Identifiers.Count);
    }

    [Fact]
    public void ApplyRewritesOnlyIdentityLines()
    {
        var applied = BasisConfigService.Apply(Settings, MyApp);
        Assert.Equal("%YAML 1.1\nPlayerSettings:\n  companyName: MyApp\n  productName: MyApp\n  bundleVersion: 1.2.0\n  applicationIdentifier:\n    Android: com.myapp.app\n    Standalone: com.myapp.app\n    iPhone: com.myapp.app\n  buildNumber:\n    Standalone: 0\n  overrideDefaultApplicationIdentifier: 1\n  AndroidBundleVersionCode: 221\n", applied);
        Assert.Empty(BasisConfigService.Drift(applied, MyApp));
        Assert.Equal(applied, BasisConfigService.Apply(applied, MyApp));
    }

    [Fact]
    public void ApplyAddsMissingPlatformsAndKeepsCrlf()
    {
        var text = "PlayerSettings:\r\n  productName: X\r\n  applicationIdentifier:\r\n    Standalone: a.b\r\n  buildNumber: {}\r\n";
        var applied = BasisConfigService.Apply(text, new BasisConfig { Identifier = "org.me.app" });
        Assert.Equal("PlayerSettings:\r\n  productName: X\r\n  applicationIdentifier:\r\n    Android: org.me.app\r\n    Standalone: org.me.app\r\n    iPhone: org.me.app\r\n  buildNumber: {}\r\n", applied);
        var empty = BasisConfigService.Apply("PlayerSettings:\n  applicationIdentifier: {}\n  x: 1\n", new BasisConfig { Identifier = "org.me.app" });
        Assert.Equal("PlayerSettings:\n  applicationIdentifier:\n    Android: org.me.app\n    Standalone: org.me.app\n    iPhone: org.me.app\n  x: 1\n", empty);
    }

    [Fact]
    public void NamesThatNeedQuotesRoundTrip()
    {
        var config = new BasisConfig { ProductName = "MyApp: Beta #2", CompanyName = "O'Neil" };
        var applied = BasisConfigService.Apply(Settings, config);
        Assert.Contains("  productName: 'MyApp: Beta #2'\n", applied);
        Assert.Contains("  companyName: O'Neil\n", applied);
        Assert.Contains("  companyName: '''Quoted'''\n", BasisConfigService.Apply(Settings, new BasisConfig { CompanyName = "'Quoted'" }));
        var identity = BasisConfigService.ReadIdentity(applied);
        Assert.Equal("MyApp: Beta #2", identity.ProductName);
        Assert.Equal("O'Neil", identity.CompanyName);
        Assert.Equal("みねたけ", BasisConfigService.ReadIdentity(BasisConfigService.Apply(Settings, new BasisConfig { ProductName = "みねたけ" })).ProductName);
    }

    [Fact]
    public void ValidateFlagsSharedAndBadIdentifiers()
    {
        Assert.All(BasisConfigService.Validate(MyApp), i => Assert.Equal(BpmIssueLevel.Info, i.Level));
        var basis = BasisConfigService.Validate(new BasisConfig { ProductName = "Basis Unity", CompanyName = "Basis Unity", Identifier = "com.Basis-Unity.Basis-Unity", Version = "1.0" });
        Assert.Contains(basis, i => i.Subject == "identifier" && i.Message.Contains("install over each other"));
        Assert.Contains(basis, i => i.Subject == "identifier" && i.Message.Contains("Android"));
        Assert.Contains(basis, i => i.Subject == "productName");
        Assert.Contains(basis, i => i.Subject == "companyName");
        Assert.Contains(BasisConfigService.Validate(new BasisConfig { ProductName = "a", Identifier = "myapp" }), i => i.Level == BpmIssueLevel.Error);
        Assert.Contains(BasisConfigService.Validate(new BasisConfig { ProductName = "a", Identifier = "com.1myapp.app" }), i => i.Level == BpmIssueLevel.Error);
        Assert.Contains(BasisConfigService.Validate(new BasisConfig { ProductName = "a", Identifier = "com.example.app" }), i => i.Message.Contains("placeholder"));
        Assert.Contains(BasisConfigService.Validate(new BasisConfig { ProductName = "a", Identifier = "org.me.app", Version = "first" }), i => i.Subject == "version");
        var typo = BasisConfigService.Validate(new BasisConfig { ProductName = "a", Identifier = "org.me.app", Features = new() { ["mainmenu"] = false, ["mainMenu"] = false } });
        Assert.Contains(typo, i => i.Subject == "features.mainmenu" && i.Message.Contains("did you mean mainMenu"));
        Assert.DoesNotContain(typo, i => i.Subject == "features.mainMenu");
    }

    [Fact]
    public void SetAndGetCoverFeaturesAndSettings()
    {
        var config = new BasisConfig();
        BasisConfigService.Set(config, "name", "MyApp");
        BasisConfigService.Set(config, "features.mainMenu", "off");
        BasisConfigService.Set(config, "settings.mainvolume", "50");
        BasisConfigService.Set(config, "settings.microphonemode", "pushtotalk");
        BasisConfigService.Set(config, "settings.showdevelopertab", "true");
        Assert.Equal("MyApp", config.ProductName);
        Assert.False(config.Features!["mainMenu"]);
        Assert.Equal("50", BasisConfigService.Get(config, "settings.mainvolume"));
        Assert.Equal("pushtotalk", BasisConfigService.Get(config, "settings.microphonemode"));
        Assert.Equal(System.Text.Json.JsonValueKind.True, config.Settings!["showdevelopertab"].ValueKind);
        Assert.Throws<ArgumentException>(() => BasisConfigService.Set(config, "features.mainMenu", "maybe"));
        Assert.Throws<ArgumentException>(() => BasisConfigService.Set(config, "colour", "red"));
        Assert.Throws<ArgumentException>(() => BasisConfigService.Set(config, "features", "off"));
        BasisConfigService.Set(config, "features.mainMenu", null);
        Assert.Null(BasisConfigService.Get(config, "features.mainMenu"));
    }

    [Fact]
    public void WriteReadKeepsUnknownKeysAndApplyRefusesWhileUnityIsOpen()
    {
        using var temp = new TempDir("basisconfig");
        var project = temp.CreateDir("project");
        File.WriteAllText(BasisConfigService.ConfigPath(project), "{ \"productName\": \"MyApp\", \"identifier\": \"com.myapp.app\", \"bundle\": { \"icon\": \"x.png\" }, \"features\": { \"mainMenu\": false } }");
        var config = BasisConfigService.Read(project)!;
        BasisConfigService.Write(project, config);
        var text = File.ReadAllText(BasisConfigService.ConfigPath(project));
        Assert.Contains("\"bundle\"", text);
        Assert.Contains("\"mainMenu\": false", text);
        Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
        File.WriteAllText(BasisConfigService.SettingsPath(project), Settings);
        Assert.Contains(BasisConfigService.Check(project), i => i.Message.Contains("doesn't match"));
        Assert.False(BasisConfigService.ApplyToProject(project, unityOpen: true).Ok);
        Assert.Equal(Settings, File.ReadAllText(BasisConfigService.SettingsPath(project)));
        Assert.True(BasisConfigService.ApplyToProject(project, unityOpen: false).Ok);
        Assert.Equal("MyApp", BasisConfigService.ReadIdentity(File.ReadAllText(BasisConfigService.SettingsPath(project))).ProductName);
        Assert.DoesNotContain(BasisConfigService.Check(project), i => i.Message.Contains("doesn't match"));
    }

    [Fact]
    public void StarterReplacesBasisDefaults()
    {
        using var temp = new TempDir("basisconfig");
        var project = temp.CreateDir("My App");
        Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
        File.WriteAllText(BasisConfigService.SettingsPath(project), Settings);
        var starter = BasisConfigService.Starter(project);
        Assert.Equal("My App", starter.ProductName);
        Assert.Null(starter.CompanyName);
        Assert.Equal("com.example.myapp", starter.Identifier);
        Assert.Equal("2.0.1", starter.Version);
    }

    [Fact]
    public void TemplateUpdatesKeepTheProjectsIdentity()
    {
        var upstream = Encoding.UTF8.GetBytes(Settings.Replace("AndroidBundleVersionCode: 221", "AndroidBundleVersionCode: 222"));
        var bytes = BpmService.TemplateBytes(BasisConfigService.ProjectSettingsFile, upstream, MyApp);
        var identity = BasisConfigService.ReadIdentity(Encoding.UTF8.GetString(bytes));
        Assert.Equal("MyApp", identity.ProductName);
        Assert.Equal("com.myapp.app", identity.Identifiers["Standalone"]);
        Assert.Contains("AndroidBundleVersionCode: 222", Encoding.UTF8.GetString(bytes));
        Assert.Equal("org.Basis-Unity.Basis-Unity", BasisConfigService.ReadIdentity(Encoding.UTF8.GetString(BpmService.TemplateBytes(BasisConfigService.ProjectSettingsFile, upstream))).Identifiers["Standalone"]);
    }
}
