using BasisPM.App.Services;

namespace BasisPM.App.ViewModels;

/// <summary>The Documentation page — links out to the docs site and community help (opens in the browser).</summary>
public sealed class DocumentationViewModel : ObservableObject
{
    public RelayCommand DocsCommand { get; }
    public RelayCommand DiscordCommand { get; }
    public RelayCommand BasisRepoCommand { get; }
    public RelayCommand PackageManagerRepoCommand { get; }

    public DocumentationViewModel()
    {
        DocsCommand = new RelayCommand(() => ExternalLink.Open("https://docs.basisvr.org"));
        DiscordCommand = new RelayCommand(() => ExternalLink.Open("https://discord.gg/v6ve6WT562"));
        BasisRepoCommand = new RelayCommand(() => ExternalLink.Open("https://github.com/BasisVR/Basis"));
        PackageManagerRepoCommand = new RelayCommand(() => ExternalLink.Open("https://github.com/BasisVR/BasisPackageManager"));
    }
}
