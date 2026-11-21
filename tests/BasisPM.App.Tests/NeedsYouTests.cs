using Avalonia.Headless.XUnit;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class NeedsYouTests
{
    private static InstallRow Row(string name) => new(new BasisInstall { RepoRoot = Path.Combine(Path.GetTempPath(), name), UnityProjectPath = Path.Combine(Path.GetTempPath(), name, "Basis"), Name = name, IsGitRepo = true });

    [AvaloniaFact]
    public void The_list_says_why_each_project_waits_and_what_its_button_does()
    {
        Localizer.Instance.SetLanguage("en");
        var model = new NeedsYouViewModel(new[]
        {
            new NeedsYouItem(Row("Avatar"), "has a paused update or branch switch", NeedsYouKind.Paused),
            new NeedsYouItem(Row("Cave"), "would conflict with your changes", NeedsYouKind.Conflicts),
        });

        Assert.Equal("2 projects need a decision before Basis can update them", model.Heading);
        Assert.Equal("Finish update", model.Items[0].ActionLabel);
        Assert.Equal("Review update", model.Items[1].ActionLabel);
        Assert.Contains("still in conflict", model.Items[0].Hint);
        Assert.Equal("Cave needs a decision before Basis can update it", new NeedsYouViewModel(new[] { model.Items[1] }).Heading);
    }

    [AvaloniaFact]
    public void Results_list_updated_projects_and_offer_the_unity_they_now_need()
    {
        Localizer.Instance.SetLanguage("en");
        var updated = new[] { new UpdatedItem(Row("Avatar"), "6000.7.0b9", false), new UpdatedItem(Row("Wispy"), "6000.7.0b9", true), new UpdatedItem(Row("Same"), null, false) };
        var model = new NeedsYouViewModel(new[] { new NeedsYouItem(Row("Cave"), "would conflict with your changes", NeedsYouKind.Conflicts) }, updated);

        Assert.Equal("Basis update results", model.Title);
        Assert.Equal("3 updated, 1 need you", model.Heading);
        Assert.True(updated[0].NeedsUnity);
        Assert.Equal("Get Unity 6000.7.0b9", updated[0].ActionLabel);
        Assert.False(updated[1].NeedsUnity);
        Assert.False(updated[2].NeedsUnity);
        Assert.Equal("Now on the latest Basis.", updated[2].Summary);
        Assert.Equal("3 updated, nothing left to do", new NeedsYouViewModel(Array.Empty<NeedsYouItem>(), updated).Heading);
    }
}
