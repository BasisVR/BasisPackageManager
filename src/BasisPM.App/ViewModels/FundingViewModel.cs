using System.Diagnostics;

namespace BasisPM.App.ViewModels;

/// <summary>The Support page — links out to the project's funding channels (opens in the browser).</summary>
public sealed class FundingViewModel : ObservableObject
{
    public RelayCommand OpenCollectiveCommand { get; }
    public RelayCommand DiscordCommand { get; }
    public RelayCommand FundingPageCommand { get; }

    public FundingViewModel()
    {
        OpenCollectiveCommand = new RelayCommand(() => Open("https://opencollective.com/basis"));
        DiscordCommand = new RelayCommand(() => Open("https://discord.gg/v6ve6WT562"));
        FundingPageCommand = new RelayCommand(() => Open("https://basisvr.org/funding"));
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
