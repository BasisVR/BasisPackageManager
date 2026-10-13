using BasisPM.App.Localization;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed record LicenseOption(ScaffoldLicense License, string Label);

public sealed class AssemblyPick : ObservableObject
{
    private bool _isSelected;

    public AssemblyPick(BasisAssembly assembly, bool selected)
    {
        Assembly = assembly;
        _isSelected = selected;
    }

    public BasisAssembly Assembly { get; }
    public string Name => Assembly.Name;
    public string Detail => Assembly.PackageId;
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }
}

public sealed record NewPackageDraft(PackageScaffoldRequest Request, bool CreateRepository, bool PrivateRepository, string? Token = null, GitHubUser? User = null);

public sealed class NewPackageViewModel : ObservableObject
{
    private readonly Func<string, string?> _conflict;
    private readonly string? _unity;
    private readonly string? _basisFrom;
    private string _displayName = "";
    private string _id;
    private string _description = "";
    private string _author;
    private bool _idEdited;
    private LicenseOption _selectedLicense;
    private bool _runtime = true;
    private bool _editor = true;
    private bool _server;
    private bool _samples;
    private bool _createRepository;
    private bool _privateRepository;
    private string _errorText = "";
    private bool _isSigningIn;

    public NewPackageViewModel(string projectName, string author, string? unity, string? basisFrom, IReadOnlyList<BasisAssembly> assemblies, bool serverAvailable, Func<string, string?> conflict)
    {
        ProjectName = projectName;
        _author = author;
        _unity = unity;
        _basisFrom = basisFrom;
        _conflict = conflict;
        ServerAvailable = serverAvailable;
        _id = PackageScaffoldService.SuggestId(author, null);
        Licenses = new[]
        {
            new LicenseOption(ScaffoldLicense.Mit, L.Tr("dialog.newPackage.license.mit")),
            new LicenseOption(ScaffoldLicense.Apache2, L.Tr("dialog.newPackage.license.apache")),
            new LicenseOption(ScaffoldLicense.None, L.Tr("dialog.newPackage.license.none")),
        };
        _selectedLicense = Licenses[0];
        Assemblies = assemblies.Where(a => !a.EditorOnly)
            .Select(a => new AssemblyPick(a, PackageScaffoldService.DefaultReferences.Contains(a.Name, StringComparer.Ordinal)))
            .ToList();
    }

    public IPublishHost? Host { get; set; }
    public Func<Task<string?>>? AskForToken { get; set; }
    public string ProjectName { get; }
    public string Subtitle => L.Tr("dialog.newPackage.subtitle", ProjectName);
    public IReadOnlyList<LicenseOption> Licenses { get; }
    public IReadOnlyList<AssemblyPick> Assemblies { get; }
    public bool HasAssemblies => Assemblies.Count > 0;
    public bool ServerAvailable { get; }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (!SetField(ref _displayName, value)) return;
            if (!_idEdited) SetSuggestedId();
        }
    }

    public string Id
    {
        get => _id;
        set
        {
            var trimmed = (value ?? "").Trim();
            if (!SetField(ref _id, trimmed)) return;
            _idEdited = !string.Equals(trimmed, PackageScaffoldService.SuggestId(_author, _displayName), StringComparison.Ordinal);
            OnPropertyChanged(nameof(AssemblyPreview));
            OnPropertyChanged(nameof(FolderPreview));
        }
    }

    public string Description { get => _description; set => SetField(ref _description, value); }

    public string Author
    {
        get => _author;
        set
        {
            if (!SetField(ref _author, value)) return;
            if (!_idEdited) SetSuggestedId();
        }
    }

    public LicenseOption SelectedLicense { get => _selectedLicense; set { if (value is not null) SetField(ref _selectedLicense, value); } }
    public bool Runtime { get => _runtime; set { if (SetField(ref _runtime, value)) OnPropertyChanged(nameof(AssemblyPreview)); } }
    public bool Editor { get => _editor; set => SetField(ref _editor, value); }
    public bool Server { get => _server; set => SetField(ref _server, value); }
    public bool Samples { get => _samples; set => SetField(ref _samples, value); }
    public bool CreateRepository { get => _createRepository; set => SetField(ref _createRepository, value); }
    public bool PrivateRepository { get => _privateRepository; set => SetField(ref _privateRepository, value); }

    public string AssemblyPreview => L.Tr("dialog.newPackage.assemblyPreview", PackageScaffoldService.AssemblyName(_id.Length == 0 ? "package" : _id));
    public string FolderPreview => L.Tr("dialog.newPackage.folderPreview", BasisDevStore.FolderName + "/" + _id);
    public string UnityText => _unity is null ? L.Tr("dialog.newPackage.unityUnknown") : L.Tr("dialog.newPackage.unity", _unity);
    public string BasisText => _basisFrom is null ? L.Tr("dialog.newPackage.basisUnknown") : L.Tr("dialog.newPackage.basis", _basisFrom);

    public string ErrorText { get => _errorText; private set { if (SetField(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _errorText.Length > 0;
    public bool IsSigningIn { get => _isSigningIn; private set { if (SetField(ref _isSigningIn, value)) OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !_isSigningIn;

    private void SetSuggestedId()
    {
        _id = PackageScaffoldService.SuggestId(_author, _displayName);
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(AssemblyPreview));
        OnPropertyChanged(nameof(FolderPreview));
    }

    public NewPackageDraft? Build()
    {
        if (string.IsNullOrWhiteSpace(_displayName)) { ErrorText = L.Tr("dialog.newPackage.error.nameMissing"); return null; }
        var problem = PackageScaffoldService.ValidateId(_id);
        if (problem != PackageIdProblem.None) { ErrorText = IdError(problem, _id); return null; }
        if (_conflict(_id) is not null) { ErrorText = L.Tr("dialog.newPackage.error.exists", _id); return null; }
        ErrorText = "";
        var request = new PackageScaffoldRequest
        {
            Id = _id,
            DisplayName = _displayName.Trim(),
            Description = _description.Trim(),
            Author = _author.Trim(),
            License = _selectedLicense.License,
            Unity = _unity,
            BasisFrom = _basisFrom,
            References = Assemblies.Where(a => a.IsSelected).Select(a => a.Name).ToList(),
            Runtime = _runtime,
            Editor = _editor,
            Server = _server && ServerAvailable,
            Samples = _samples,
        };
        return new NewPackageDraft(request, _createRepository, _privateRepository);
    }

    public async Task<NewPackageDraft?> ConfirmAsync()
    {
        if (_isSigningIn || Build() is not { } draft) return null;
        if (!draft.CreateRepository || Host is null) return draft;
        IsSigningIn = true;
        try
        {
            var (token, user, error) = await GitHubSignIn.SignInAsync(Host, AskForToken);
            if (token is null || user is null)
            {
                ErrorText = error;
                return null;
            }
            return draft with { Token = token, User = user };
        }
        finally { IsSigningIn = false; }
    }

    public static string IdError(PackageIdProblem problem, string id) => problem switch
    {
        PackageIdProblem.Missing => L.Tr("dialog.newPackage.error.idMissing"),
        PackageIdProblem.TooLong => L.Tr("dialog.newPackage.error.idTooLong"),
        PackageIdProblem.NoDot => L.Tr("dialog.newPackage.error.idNoDot", id),
        PackageIdProblem.Reserved => L.Tr("dialog.newPackage.error.idReserved"),
        _ => L.Tr("dialog.newPackage.error.idCharacters", id),
    };
}
