namespace BasisPM.App.ViewModels;

/// <summary>
/// The Home page (the first page the app opens on): announcements and links out to docs, help, source and funding.
/// It composes the existing view-models so their logic (the announcement feed + unread tracking
/// and the link-out commands) is reused rather than duplicated; the view binds nested properties
/// (e.g. <c>Documentation.DocsCommand</c>, <c>Support.OpenCollectiveCommand</c>).
/// </summary>
public sealed class CommunityViewModel : ObservableObject
{
    public AnnouncementsViewModel Announcements { get; }
    public DocumentationViewModel Documentation { get; }
    public FundingViewModel Support { get; }
    public RelayCommand OpenProjectsCommand { get; }

    public CommunityViewModel(
        AnnouncementsViewModel announcements,
        DocumentationViewModel documentation,
        FundingViewModel support,
        Action openProjects)
    {
        Announcements = announcements;
        Documentation = documentation;
        Support = support;
        OpenProjectsCommand = new RelayCommand(openProjects);
    }
}
