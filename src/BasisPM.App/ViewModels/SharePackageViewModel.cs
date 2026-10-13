using BasisPM.App.Localization;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed record SharePackageDraft(string Branch, string? Message, string Token, GitHubUser User);

public sealed class SharePackageViewModel : ObservableObject
{
    private readonly ISubmitPrHost _host;
    private string _branch;
    private string _message = "";
    private string _errorText = "";
    private bool _isSigningIn;

    public SharePackageViewModel(ISubmitPrHost host, string displayName, string upstream, bool hasUncommitted, string branch)
    {
        _host = host;
        DisplayName = displayName;
        Upstream = upstream;
        NeedsMessage = hasUncommitted;
        _branch = branch;
    }

    public string DisplayName { get; }
    public string Upstream { get; }
    public bool NeedsMessage { get; }
    public string Title => L.Tr("dialog.share.title", DisplayName);
    public string Body => L.Tr("dialog.share.body", Upstream);
    public string Branch { get => _branch; set => SetField(ref _branch, value); }
    public string Message { get => _message; set => SetField(ref _message, value); }
    public string ErrorText { get => _errorText; private set { if (SetField(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _errorText.Length > 0;
    public bool IsSigningIn { get => _isSigningIn; private set { if (SetField(ref _isSigningIn, value)) OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !_isSigningIn;
    public Func<Task<string?>>? AskForToken { get; set; }

    public async Task<SharePackageDraft?> SignInAsync()
    {
        if (_isSigningIn) return null;
        if (string.IsNullOrWhiteSpace(_branch))
        {
            ErrorText = L.Tr("dialog.share.needBranch");
            return null;
        }
        if (NeedsMessage && string.IsNullOrWhiteSpace(_message))
        {
            ErrorText = L.Tr("dialog.share.needMessage");
            return null;
        }
        IsSigningIn = true;
        ErrorText = "";
        try
        {
            var token = await _host.GetTokenAsync();
            if (string.IsNullOrEmpty(token) && AskForToken is not null && await AskForToken() is { Length: > 0 } entered)
            {
                _host.RememberToken(entered.Trim());
                token = await _host.GetTokenAsync();
            }
            if (string.IsNullOrEmpty(token))
            {
                ErrorText = L.Tr("develop.status.signInFirst");
                return null;
            }
            var user = await _host.GetUserAsync(token);
            if (user is null)
            {
                ErrorText = L.Tr("develop.status.loginUnverified");
                return null;
            }
            return new SharePackageDraft(_branch.Trim(), NeedsMessage ? _message.Trim() : null, token, user);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Signing in to share package changes", ex);
            ErrorText = L.Tr("dialog.share.failed", ex.Message);
            return null;
        }
        finally { IsSigningIn = false; }
    }
}
