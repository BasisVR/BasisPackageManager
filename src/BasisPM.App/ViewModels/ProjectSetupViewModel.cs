using System.Collections.ObjectModel;
using System.Text.Json;
using BasisPM.App.Localization;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed class ProjectSetupViewModel : ObservableObject
{
    private readonly string _projectPath;
    private readonly Func<string, bool> _unityOpen;
    private BasisConfig _loaded = new();
    private string _productName = "", _companyName = "", _identifier = "", _version = "";
    private string _status = "";
    private bool _statusIsError, _exists, _dirty;

    public ProjectSetupViewModel(string projectPath, string projectName, Func<string, bool>? unityOpen = null, Func<Task>? openParts = null, Func<Task>? openBackup = null, Func<Task>? openStorage = null)
    {
        _projectPath = projectPath;
        PartsCommand = Tool(openParts);
        BackupCommand = Tool(openBackup);
        StorageCommand = Tool(openStorage);
        HasParts = openParts is not null;
        HasBackup = openBackup is not null;
        HasStorage = openStorage is not null;
        ProjectName = projectName;
        _unityOpen = unityOpen ?? UnityOpenProbe.IsOpen;
        SaveCommand = new RelayCommand(Save, () => !HasErrors);
        AddSettingCommand = new RelayCommand(AddSetting);
        RemoveSettingCommand = new RelayCommand<SetupSettingRow>(row =>
        {
            if (row is not null && Settings.Remove(row)) Changed();
            return Task.CompletedTask;
        });
        SuggestIdentifierCommand = new RelayCommand(() => Identifier = BasisConfigService.SuggestIdentifier(Blank(_companyName), Blank(_productName) ?? ProjectName));
        Load();
    }

    public string ProjectName { get; }
    public string Title => L.Tr("dialog.setup.title", ProjectName);
    public string Heading => L.Tr("dialog.setup.heading", ProjectName);
    public string FilePath => BasisConfigService.ConfigPath(_projectPath);
    public string FileText => _exists ? L.Tr("dialog.setup.file", FilePath) : L.Tr("dialog.setup.fileNew", FilePath);
    public ObservableCollection<SetupFeatureRow> Features { get; } = new();
    public ObservableCollection<SetupSettingRow> Settings { get; } = new();
    public ObservableCollection<SetupIssueRow> Issues { get; } = new();
    public RelayCommand SaveCommand { get; }
    public RelayCommand AddSettingCommand { get; }
    public RelayCommand<SetupSettingRow> RemoveSettingCommand { get; }
    public RelayCommand SuggestIdentifierCommand { get; }
    public RelayCommand PartsCommand { get; }
    public RelayCommand BackupCommand { get; }
    public RelayCommand StorageCommand { get; }
    public bool HasParts { get; }
    public bool HasBackup { get; }
    public bool HasStorage { get; }
    public bool HasTools => HasParts || HasBackup || HasStorage;
    public event Action? CloseRequested;

    public string ProductName { get => _productName; set { if (SetField(ref _productName, value ?? "")) Changed(); } }
    public string CompanyName { get => _companyName; set { if (SetField(ref _companyName, value ?? "")) Changed(); } }
    public string Identifier { get => _identifier; set { if (SetField(ref _identifier, value ?? "")) Changed(); } }
    public string Version { get => _version; set { if (SetField(ref _version, value ?? "")) Changed(); } }
    public bool HasSettings => Settings.Count > 0;
    public bool HasIssues => Issues.Count > 0;
    public bool HasErrors => Issues.Any(i => i.IsError);
    public bool IsDirty
    {
        get => _dirty;
        private set
        {
            if (!SetField(ref _dirty, value)) return;
            PartsCommand?.Raise();
            BackupCommand?.Raise();
            StorageCommand?.Raise();
        }
    }
    public string StatusText { get => _status; private set { if (SetField(ref _status, value)) RaiseStatus(); } }
    public bool HasInfoStatus => _status.Length > 0 && !_statusIsError;
    public bool HasErrorStatus => _status.Length > 0 && _statusIsError;

    public void Load()
    {
        BasisConfig? config;
        try { config = BasisConfigService.Read(_projectPath); }
        catch (InvalidDataException ex)
        {
            SetStatus(ex.Message, true);
            config = null;
        }
        _exists = config is not null;
        _loaded = config ?? BasisConfigService.Starter(_projectPath);
        _productName = _loaded.ProductName ?? "";
        _companyName = _loaded.CompanyName ?? "";
        _identifier = _loaded.Identifier ?? "";
        _version = _loaded.Version ?? "";
        foreach (var row in Features) row.PropertyChanged -= OnRowChanged;
        foreach (var row in Settings) row.PropertyChanged -= OnRowChanged;
        Features.Clear();
        Settings.Clear();
        var features = _loaded.Features ?? new Dictionary<string, bool>();
        foreach (var key in BasisConfigService.KnownFeatures.Keys.Concat(features.Keys.Where(k => !BasisConfigService.KnownFeatures.ContainsKey(k))))
        {
            var row = new SetupFeatureRow(key, FeatureLabel(key), FeatureDetail(key), !features.TryGetValue(key, out var on) || on, features.ContainsKey(key));
            row.PropertyChanged += OnRowChanged;
            Features.Add(row);
        }
        foreach (var (key, value) in _loaded.Settings ?? new Dictionary<string, JsonElement>())
        {
            var row = new SetupSettingRow(key, value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText());
            row.PropertyChanged += OnRowChanged;
            Settings.Add(row);
        }
        foreach (var name in new[] { nameof(ProductName), nameof(CompanyName), nameof(Identifier), nameof(Version), nameof(FileText), nameof(HasSettings) }) OnPropertyChanged(name);
        Revalidate();
        IsDirty = false;
    }

    public BasisConfig Build()
    {
        var json = JsonSerializer.Serialize(_loaded, BasisConfigJson);
        var config = JsonSerializer.Deserialize<BasisConfig>(json, BasisConfigJson) ?? new BasisConfig();
        config.ProductName = Blank(_productName);
        config.CompanyName = Blank(_companyName);
        config.Identifier = Blank(_identifier);
        config.Version = Blank(_version);
        config.Features = Features.Where(f => !f.IsOn || f.WasListed).ToDictionary(f => f.Key, f => f.IsOn, StringComparer.Ordinal);
        config.Settings = Settings.Where(s => !string.IsNullOrWhiteSpace(s.Key)).GroupBy(s => s.Key.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => BasisConfigService.SettingValue(g.Last().Value.Trim()), StringComparer.Ordinal);
        return config;
    }

    public Task Save()
    {
        var config = Build();
        if (BasisConfigService.Validate(config).Any(i => i.Level == BpmIssueLevel.Error)) return Task.CompletedTask;
        try
        {
            BasisConfigService.Write(_projectPath, config);
            _loaded = config;
            _exists = true;
            foreach (var row in Features) row.WasListed = config.Features?.ContainsKey(row.Key) == true;
            OnPropertyChanged(nameof(FileText));
            if (!File.Exists(BasisConfigService.SettingsPath(_projectPath))) SetStatus(L.Tr("dialog.setup.savedNoSettings"), false);
            else if (_unityOpen(_projectPath)) SetStatus(L.Tr("dialog.setup.savedUnityOpen"), false);
            else
            {
                var applied = BasisConfigService.ApplyToProject(_projectPath, false);
                SetStatus(applied.Ok ? L.Tr("dialog.setup.saved") : string.Join(" ", applied.Issues.Select(i => i.Message)), !applied.Ok);
            }
            IsDirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(L.Tr("dialog.setup.saveFailed", ex.Message), true);
        }
        Revalidate();
        return Task.CompletedTask;
    }

    private RelayCommand Tool(Func<Task>? open) => new(async () =>
    {
        if (open is null) return;
        CloseRequested?.Invoke();
        await open();
    }, () => open is not null && !_dirty);

    private void AddSetting()
    {
        var row = new SetupSettingRow("", "");
        row.PropertyChanged += OnRowChanged;
        Settings.Add(row);
        Changed();
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Changed();

    private void Changed()
    {
        IsDirty = true;
        OnPropertyChanged(nameof(HasSettings));
        Revalidate();
    }

    private void Revalidate()
    {
        var config = Build();
        Issues.Clear();
        foreach (var issue in BasisConfigService.Validate(config).Where(i => i.Subject != "server")) Issues.Add(new SetupIssueRow(issue.Message, issue.Level == BpmIssueLevel.Error, issue.Level == BpmIssueLevel.Warning));
        foreach (var key in Settings.Where(s => string.IsNullOrWhiteSpace(s.Key) && !string.IsNullOrWhiteSpace(s.Value))) Issues.Add(new SetupIssueRow(L.Tr("dialog.setup.settingNoKey"), false, true));
        if (Blank(_productName) is { } product && !BasisConfigService.IsBasisName(product) || Blank(_companyName) is { } company && !BasisConfigService.IsBasisName(company))
            Issues.Add(new SetupIssueRow(L.Tr("dialog.setup.serverNote"), false, false));
        var settingsFile = BasisConfigService.SettingsPath(_projectPath);
        if (!_dirty && _exists && File.Exists(settingsFile) && BasisConfigService.Drift(File.ReadAllText(settingsFile), config) is { Count: > 0 } drift)
            Issues.Add(new SetupIssueRow(L.Tr("dialog.setup.drift", string.Join(", ", drift.Select(d => d.Field))), false, true));
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(HasErrors));
        SaveCommand.Raise();
    }

    private void SetStatus(string text, bool error)
    {
        _statusIsError = error;
        if (!SetField(ref _status, text, nameof(StatusText))) RaiseStatus();
    }

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(HasInfoStatus));
        OnPropertyChanged(nameof(HasErrorStatus));
    }

    private static string FeatureLabel(string key) => L.Tr("dialog.setup.feature." + key) is var text && text != "dialog.setup.feature." + key ? text : key;

    private static string FeatureDetail(string key) => L.Tr("dialog.setup.featureDetail." + key) is var text && text != "dialog.setup.featureDetail." + key ? text : L.Tr("dialog.setup.featureCustom");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static readonly JsonSerializerOptions BasisConfigJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
}

public sealed class SetupFeatureRow : ObservableObject
{
    private bool _isOn;

    public SetupFeatureRow(string key, string label, string detail, bool isOn, bool wasListed)
    {
        Key = key;
        Label = label;
        Detail = detail;
        _isOn = isOn;
        WasListed = wasListed;
    }

    public string Key { get; }
    public string Label { get; }
    public string Detail { get; }
    public bool WasListed { get; set; }
    public bool IsOn { get => _isOn; set => SetField(ref _isOn, value); }
}

public sealed class SetupSettingRow : ObservableObject
{
    private string _key, _value;

    public SetupSettingRow(string key, string value)
    {
        _key = key;
        _value = value;
    }

    public string Key { get => _key; set => SetField(ref _key, value ?? ""); }
    public string Value { get => _value; set => SetField(ref _value, value ?? ""); }
}

public sealed record SetupIssueRow(string Message, bool IsError, bool IsWarning)
{
    public bool IsInfo => !IsError && !IsWarning;
}
