using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Compendium.Ux.Services;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class ProvidersSectionViewModel : ObservableObject
{

    [ObservableProperty] private string _defaultModel = string.Empty;

    [ObservableProperty] private string _fastModel = string.Empty;

    public ObservableCollection<ProviderViewModel> Providers { get; } = [];

    private readonly IDialogService _dialogService;

    public ProvidersSectionViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public void LoadFrom(
        ProviderSettings[] providers,
        string? defaultModel,
        string? fastModel)
    {

        DefaultModel = defaultModel ?? string.Empty;

        FastModel = fastModel ?? string.Empty;

        Providers.Clear();

        foreach (ProviderSettings provider in providers)
        {

            Providers.Add(new ProviderViewModel(provider, _dialogService));

        }

    }

    public ProviderSettings[] BuildProviders() =>
        [.. Providers.Select(static provider => provider.Build())];

    [RelayCommand]
    private void AddProvider() =>
        Providers.Add(new ProviderViewModel(new ProviderSettings(), _dialogService));

    [RelayCommand]
    private async Task RemoveProviderAsync(ProviderViewModel? provider)
    {

        if (provider is null)
        {

            return;

        }

        bool confirmed = await _dialogService.ShowConfirmAsync(
            "Remove Provider",
            $"Are you sure you want to remove the provider '{provider.Name}'? This action cannot be undone.",
            "Remove",
            "Cancel");

        if (!confirmed)
        {

            return;

        }

        Providers.Remove(provider);

    }

    public sealed partial class ProviderViewModel : ObservableObject
    {

        [ObservableProperty] private string _name = string.Empty;

        [ObservableProperty] private AiProviderKind _type;

        [ObservableProperty] private string _endpoint = string.Empty;

        [ObservableProperty] private string _credentialEnvironmentVariable = string.Empty;

        [ObservableProperty] private int _contextWindowLimit;

        public ObservableCollection<ModelEntryViewModel> Models { get; } = [];

        private ProviderSettings _snapshot;

        private readonly IDialogService _dialogService;

        public ProviderViewModel(ProviderSettings snapshot, IDialogService dialogService)
        {

            _snapshot = snapshot;

            _dialogService = dialogService;

            LoadFrom(snapshot);

        }

        public void LoadFrom(ProviderSettings snapshot)
        {

            _snapshot = snapshot;

            Name = snapshot.Name;

            Type = snapshot.Type;

            Endpoint = snapshot.Endpoint;

            CredentialEnvironmentVariable =
                snapshot.CredentialEnvironmentVariable ?? string.Empty;

            ContextWindowLimit = snapshot.ContextWindowLimit;

            Models.Clear();

            foreach (ModelEntry model in snapshot.Models)
            {

                Models.Add(new ModelEntryViewModel(model, _dialogService));

            }

        }

        public ProviderSettings Build()
        {
            // Validate environment variable name
            if (!ConfigurationInputValidator.TryValidateEnvironmentVariableName(
                CredentialEnvironmentVariable,
                out string? envVarError))
            {
                throw new InvalidOperationException(
                    $"Provider '{Name}': {envVarError}");
            }

            return _snapshot with
            {
                Name = Name,
                Type = Type,
                Endpoint = Endpoint,
                CredentialEnvironmentVariable =
                    NullIfWhiteSpace(CredentialEnvironmentVariable),
                Models = [.. Models.Select(static model => model.Build())],
                ContextWindowLimit = ContextWindowLimit,
            };
        }

        [RelayCommand]
        private void AddModel() =>
            Models.Add(new ModelEntryViewModel(new ModelEntry(), _dialogService));

        [RelayCommand]
        private async Task RemoveModelAsync(ModelEntryViewModel? model)
        {

            if (model is null)
            {

                return;

            }

            bool confirmed = await _dialogService.ShowConfirmAsync(
                "Remove Model",
                $"Are you sure you want to remove the model '{model.Name}'? This action cannot be undone.",
                "Remove",
                "Cancel");

            if (!confirmed)
            {

                return;

            }

            Models.Remove(model);

        }

    }

    public sealed partial class ModelEntryViewModel : ObservableObject
    {

        [ObservableProperty] private string _name = string.Empty;

        [ObservableProperty] private bool _supportsVision;

        [ObservableProperty] private bool _hasReasoning;

        [ObservableProperty] private ReasoningWireDialect _reasoningDialect =
            ReasoningWireDialect.Standard;

        [ObservableProperty] private bool _hasReasoningMaxBudgetTokens;

        [ObservableProperty] private int _reasoningMaxBudgetTokens = 1;

        private ModelEntry _snapshot;

        private readonly IDialogService _dialogService;

        public ModelEntryViewModel(ModelEntry snapshot, IDialogService dialogService)
        {

            _snapshot = snapshot;

            _dialogService = dialogService;

            LoadFrom(snapshot);

        }

        public IReadOnlyList<ReasoningWireDialect> ReasoningWireDialectValues { get; } =
            Enum.GetValues<ReasoningWireDialect>();

        public void LoadFrom(ModelEntry snapshot)
        {

            _snapshot = snapshot;

            Name = snapshot.Name;

            SupportsVision = snapshot.SupportsVision;

            HasReasoning = snapshot.WireDialect is not null;

            ReasoningDialect = snapshot.WireDialect
                ?? ReasoningWireDialect.Standard;

            HasReasoningMaxBudgetTokens = snapshot.MaxBudgetTokens is not null;

            ReasoningMaxBudgetTokens = snapshot.MaxBudgetTokens ?? 1;

        }

        public ModelEntry Build() => _snapshot with
        {
            Name = Name,
            SupportsVision = SupportsVision,
            WireDialect = HasReasoning ? ReasoningDialect : null,
            MaxBudgetTokens = HasReasoning && HasReasoningMaxBudgetTokens
                ? ReasoningMaxBudgetTokens
                : null,
        };

    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value;

}