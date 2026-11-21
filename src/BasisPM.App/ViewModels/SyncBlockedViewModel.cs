using BasisPM.App.Localization;

namespace BasisPM.App.ViewModels;

public sealed class SyncBlockedViewModel
{
    public SyncBlockedViewModel(string projectName, string upstream, IReadOnlyList<string> files)
    {
        ProjectName = projectName;
        Upstream = upstream;
        Files = files;
    }

    public string ProjectName { get; }
    public string Upstream { get; }
    public IReadOnlyList<string> Files { get; }
    public string Heading => Files.Count == 1 ? L.Tr("dialog.syncBlocked.headingOne", ProjectName) : L.Tr("dialog.syncBlocked.heading", ProjectName, Files.Count);
    public string Intro => L.Tr("dialog.syncBlocked.intro", Upstream);
}
