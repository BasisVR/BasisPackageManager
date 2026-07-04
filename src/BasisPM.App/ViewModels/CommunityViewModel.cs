namespace BasisPM.App.ViewModels;

/// <summary>
/// The merged Community page — announcements, documentation and support/funding in one panel.
/// It composes the existing view-models so their logic (the announcement feed + unread tracking
/// and the link-out commands) is reused rather than duplicated; the view binds nested properties
/// (e.g. <c>Documentation.DocsCommand</c>, <c>Support.OpenCollectiveCommand</c>).
/// </summary>
public sealed class CommunityViewModel : ObservableObject
{
    public AnnouncementsViewModel Announcements { get; }
    public DocumentationViewModel Documentation { get; }
    public FundingViewModel Support { get; }

    public CommunityViewModel(
        AnnouncementsViewModel announcements,
        DocumentationViewModel documentation,
        FundingViewModel support)
    {
        Announcements = announcements;
        Documentation = documentation;
        Support = support;
    }
}
