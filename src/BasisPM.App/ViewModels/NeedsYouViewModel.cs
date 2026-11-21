using BasisPM.App.Localization;

namespace BasisPM.App.ViewModels;

public enum NeedsYouKind { Paused, Conflicts, Other }

public sealed class NeedsYouItem
{
    public NeedsYouItem(InstallRow row, string reason, NeedsYouKind kind)
    {
        Row = row;
        Reason = reason;
        Kind = kind;
    }

    public InstallRow Row { get; }
    public string Project => Row.Name;
    public string Reason { get; }
    public NeedsYouKind Kind { get; }
    public string Hint => Kind switch
    {
        NeedsYouKind.Paused => L.Tr("dialog.needsYou.hint.paused"),
        NeedsYouKind.Conflicts => L.Tr("dialog.needsYou.hint.conflicts"),
        _ => L.Tr("dialog.needsYou.hint.other"),
    };
    public string ActionLabel => L.Tr(Kind switch
    {
        NeedsYouKind.Paused => "dialog.needsYou.action.finish",
        NeedsYouKind.Conflicts => "dialog.needsYou.action.review",
        _ => "dialog.needsYou.action.open",
    });
    public string Folder => Row.RepoRoot;
}

public sealed class UpdatedItem
{
    public UpdatedItem(InstallRow row, string? newUnity, bool unityInstalled)
    {
        Row = row;
        NewUnity = newUnity;
        UnityInstalled = unityInstalled;
    }

    public InstallRow Row { get; }
    public string Project => Row.Name;
    public string? NewUnity { get; }
    public bool UnityInstalled { get; }
    public string Summary => NewUnity is null ? L.Tr("dialog.needsYou.updated.done") : L.Tr("dialog.needsYou.updated.unity", NewUnity);
    public bool NeedsUnity => NewUnity is not null && !UnityInstalled;
    public string UnityHint => L.Tr("dialog.needsYou.updated.unityMissing", NewUnity ?? "");
    public string ActionLabel => L.Tr("dialog.needsYou.action.getUnity", NewUnity ?? "");
}

public sealed class NeedsYouViewModel
{
    public NeedsYouViewModel(IReadOnlyList<NeedsYouItem> items, IReadOnlyList<UpdatedItem>? updated = null)
    {
        Items = items;
        Updated = updated ?? Array.Empty<UpdatedItem>();
    }

    public IReadOnlyList<NeedsYouItem> Items { get; }
    public IReadOnlyList<UpdatedItem> Updated { get; }
    public bool HasItems => Items.Count > 0;
    public bool HasUpdated => Updated.Count > 0;
    public string Title => L.Tr(HasUpdated ? "dialog.needsYou.titleResults" : "dialog.needsYou.title");
    public string Heading => HasUpdated
        ? Items.Count == 0 ? L.Tr("dialog.needsYou.resultsAllDone", Updated.Count) : L.Tr("dialog.needsYou.results", Updated.Count, Items.Count)
        : Items.Count == 1 ? L.Tr("dialog.needsYou.headingOne", Items[0].Project) : L.Tr("dialog.needsYou.heading", Items.Count);
    public string Intro => L.Tr(HasItems ? "dialog.needsYou.intro" : "dialog.needsYou.introDone");
    public string UpdatedHeading => L.Tr("dialog.needsYou.section.updated", Updated.Count);
    public string ItemsHeading => L.Tr("dialog.needsYou.section.needsYou", Items.Count);
}
