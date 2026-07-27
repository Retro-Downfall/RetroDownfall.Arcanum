using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class ProvidersSectionViewModel : ObservableObject
{

    [ObservableProperty] private string _defaultModel = string.Empty;

    [ObservableProperty] private string _fastModel = string.Empty;

    public ObservableCollection<ProviderViewModel> Providers { get; } = [];

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

            Providers.Add(new ProviderViewModel(provider));

        }

    }

    public ProviderSettings[] BuildProviders() =>
        [.. Providers.Select(static provider => provider.Build())];

    [RelayCommand]
    private void AddProvider() =>
        Providers.Add(new ProviderViewModel(new ProviderSettings()));

    [RelayCommand]
    private void RemoveProvider(ProviderViewModel? provider)
    {

        if (provider is not null)
        {

            Providers.Remove(provider);

        }

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

        public ProviderViewModel(ProviderSettings snapshot)
        {

            _snapshot = snapshot;

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

                Models.Add(new ModelEntryViewModel(model));

            }

        }

        public ProviderSettings Build() => _snapshot with
        {
            Name = Name,
            Type = Type,
            Endpoint = Endpoint,
            CredentialEnvironmentVariable =
                NullIfWhiteSpace(CredentialEnvironmentVariable),
            Models = [.. Models.Select(static model => model.Build())],
            ContextWindowLimit = ContextWindowLimit,
        };

        [RelayCommand]
        private void AddModel() =>
            Models.Add(new ModelEntryViewModel(new ModelEntry()));

        [RelayCommand]
        private void RemoveModel(ModelEntryViewModel? model)
        {

            if (model is not null)
            {

                Models.Remove(model);

            }

        }

    }

    public sealed partial class ModelEntryViewModel : ObservableObject
    {

        [ObservableProperty] private string _name = string.Empty;

        [ObservableProperty] private bool _supportsVision;

        [ObservableProperty] private bool _hasReasoning;

        [ObservableProperty] private ReasoningControlSupport _reasoningControl;

        [ObservableProperty] private bool _reasoningSupportsSummary;

        [ObservableProperty] private bool _reasoningSupportsFull;

        [ObservableProperty] private bool _reasoningSupportsStreaming;

        [ObservableProperty] private bool _reasoningReportsReasoningTokens;

        [ObservableProperty] private bool _reasoningAllowsClientOutput;

        [ObservableProperty] private ReasoningWireDialect _reasoningDialect =
            ReasoningWireDialect.Standard;

        [ObservableProperty] private bool _hasReasoningMaxBudgetTokens;

        [ObservableProperty] private int _reasoningMaxBudgetTokens = 1;

        private ModelEntry _snapshot;

        public ModelEntryViewModel(ModelEntry snapshot)
        {

            _snapshot = snapshot;

            LoadFrom(snapshot);

        }

        public IReadOnlyList<ReasoningControlSupport> ReasoningControlSupportValues { get; } =
            Enum.GetValues<ReasoningControlSupport>();

        public IReadOnlyList<ReasoningWireDialect> ReasoningWireDialectValues { get; } =
            Enum.GetValues<ReasoningWireDialect>();

        public ReasoningCapabilities? Reasoning => BuildReasoning();

        public void LoadFrom(ModelEntry snapshot)
        {

            _snapshot = snapshot;

            Name = snapshot.Name;

            SupportsVision = snapshot.SupportsVision;

            ReasoningCapabilities? reasoning = snapshot.Reasoning;

            HasReasoning = reasoning is not null;

            ReasoningControl = reasoning?.ControlSupport
                ?? ReasoningControlSupport.None;

            ReasoningSupportsSummary = reasoning?.SupportsSummary == true;

            ReasoningSupportsFull = reasoning?.SupportsFull == true;

            ReasoningSupportsStreaming = reasoning?.SupportsStreaming == true;

            ReasoningReportsReasoningTokens = reasoning?.ReportsReasoningTokens == true;

            ReasoningAllowsClientOutput = reasoning?.AllowsClientOutput == true;

            ReasoningDialect = reasoning?.WireDialect
                ?? ReasoningWireDialect.Standard;

            HasReasoningMaxBudgetTokens = reasoning?.MaxBudgetTokens is not null;

            ReasoningMaxBudgetTokens = reasoning?.MaxBudgetTokens ?? 1;

        }

        public ModelEntry Build() => _snapshot with
        {
            Name = Name,
            SupportsVision = SupportsVision,
            Reasoning = BuildReasoning(),
        };

        private ReasoningCapabilities? BuildReasoning()
        {

            if (!HasReasoning)
            {

                return null;

            }

            return (_snapshot.Reasoning ?? new ReasoningCapabilities()) with
            {
                ControlSupport = ReasoningControl,
                SupportsSummary = ReasoningSupportsSummary,
                SupportsFull = ReasoningSupportsFull,
                SupportsStreaming = ReasoningSupportsStreaming,
                ReportsReasoningTokens = ReasoningReportsReasoningTokens,
                AllowsClientOutput = ReasoningAllowsClientOutput,
                WireDialect = ReasoningDialect,
                MaxBudgetTokens = HasReasoningMaxBudgetTokens
                    ? ReasoningMaxBudgetTokens
                    : null,
            };

        }

    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value;

}
