using System.Collections.ObjectModel;
using BasisPM.App.Localization;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed class LicensesViewModel : ObservableObject
{
    public const string NoticesFileName = "THIRD-PARTY-NOTICES.md";

    private readonly string _unityProjectPath;
    private LicenseReport? _report;
    private bool _includeUnity;
    private bool _isLoading;
    private string _status = "";
    private bool _statusIsError;
    private int _generation;

    public LicensesViewModel(string unityProjectPath, string projectName)
    {
        _unityProjectPath = unityProjectPath;
        ProjectName = projectName;
        RefreshCommand = new RelayCommand(LoadAsync);
    }

    public string ProjectName { get; }
    public ObservableCollection<LicenseRow> Rows { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public string Heading => L.Tr("dialog.licenses.heading", ProjectName);
    public bool IsLoading { get => _isLoading; private set { if (SetField(ref _isLoading, value)) RaiseSummary(); } }
    public bool HasRows => Rows.Count > 0;
    public bool ShowEmpty => !_isLoading && _report is not null && Rows.Count == 0;
    public bool CanExport => !_isLoading && Rows.Count > 0;
    public string Markdown => _report is null ? "" : LicenseReportService.ToMarkdown(_report);
    public string StatusText { get => _status; private set { if (SetField(ref _status, value)) RaiseStatus(); } }
    public bool HasStatus => _status.Length > 0;
    public bool HasInfoStatus => HasStatus && !_statusIsError;
    public bool HasErrorStatus => HasStatus && _statusIsError;
    public bool StatusIsError { get => _statusIsError; private set { if (SetField(ref _statusIsError, value)) RaiseStatus(); } }
    public bool HasWarnings => _report is { } report && (report.Missing.Count > 0 || report.Copyleft.Count > 0);

    public bool IncludeUnity
    {
        get => _includeUnity;
        set { if (SetField(ref _includeUnity, value)) _ = LoadAsync(); }
    }

    public string SummaryText
    {
        get
        {
            if (_isLoading) return L.Tr("dialog.licenses.loading");
            if (_report is not { } report) return "";
            var parts = new List<string> { L.Tr("dialog.licenses.summary", report.Packages.Count) };
            if (report.Missing.Count > 0) parts.Add(L.Tr("dialog.licenses.summaryMissing", report.Missing.Count));
            if (report.Copyleft.Count > 0) parts.Add(L.Tr("dialog.licenses.summaryCopyleft", report.Copyleft.Count));
            return string.Join(" ", parts);
        }
    }

    public async Task LoadAsync()
    {
        var generation = ++_generation;
        IsLoading = true;
        StatusText = "";
        try
        {
            var report = await LicenseReportService.BuildAsync(_unityProjectPath, ProjectName, _includeUnity);
            if (generation != _generation) return;
            _report = report;
            Rows.Clear();
            foreach (var package in report.Packages) Rows.Add(new LicenseRow(package));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagnosticLog.Write($"Listing the licenses of {_unityProjectPath}", ex);
            if (generation != _generation) return;
            ReportError(L.Tr("dialog.licenses.loadFailed", ex.Message));
        }
        finally
        {
            if (generation == _generation) IsLoading = false;
        }
    }

    public void ReportCopied()
    {
        StatusIsError = false;
        StatusText = L.Tr("dialog.licenses.copied");
    }

    public void ReportSaved(string path)
    {
        StatusIsError = false;
        StatusText = L.Tr("dialog.licenses.saved", path);
    }

    public void ReportError(string message)
    {
        StatusIsError = true;
        StatusText = message;
    }

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(HasInfoStatus));
        OnPropertyChanged(nameof(HasErrorStatus));
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(HasWarnings));
    }
}

public sealed class LicenseRow
{
    public LicenseRow(PackageLicense package) => Package = package;

    public PackageLicense Package { get; }
    public string Name => Package.DisplayName;
    public string Id => Package.Id;
    public string Detail => Package.Version is null ? Package.Id : L.Tr("dialog.licenses.idVersion", Package.Id, Package.Version);
    public bool IsMissing => Package.Concern == LicenseConcern.Missing;
    public bool IsCopyleft => Package.Concern == LicenseConcern.Copyleft;
    public bool HasConcern => Package.Concern != LicenseConcern.None;

    public string LicenseText => Package.License
        ?? (Package.DetectedLicense is { } detected ? L.Tr("dialog.licenses.detected", detected) : L.Tr("dialog.licenses.noLicense"));

    public string LicenseTooltip => Package.Files.Count == 0
        ? L.Tr("dialog.licenses.noFiles")
        : L.Tr("dialog.licenses.files", string.Join(", ", Package.Files.Select(f => f.Name)));

    public string ConcernText => Package.Concern switch
    {
        LicenseConcern.Missing => L.Tr("dialog.licenses.missingTip"),
        LicenseConcern.Copyleft => L.Tr("dialog.licenses.copyleftTip"),
        _ => "",
    };
}
