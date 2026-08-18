using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Compendium.Ux.Services;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class ProvidersSectionViewModel : ObservableObject
{

    [ObservableProperty] private string _defaultModel = string.Empty;

    [ObservableProperty] private string _fastModel = string.Empty;

    public ObservableCollection<ProviderViewModel> Providers { get; } = [];

    private readonly IDialogService _dialogService;

    private readonly IFamiliarProbeClient? _probeClient;

    public ProvidersSectionViewModel(
        IDialogService dialogService,
        IFamiliarProbeClient? probeClient = null)
    {
        _dialogService = dialogService;

        _probeClient = probeClient;
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

            Providers.Add(new ProviderViewModel(provider, _dialogService, _probeClient));

        }

    }

    /// <summary>
    /// Projects every row for persistence. Throws when a row cannot be saved as it stands, which is
    /// what routes the problem to the operator through the Save failure dialog.
    /// </summary>
    public ProviderSettings[] BuildProviders() =>
        [.. Providers.Select(static provider => provider.Build())];

    /// <summary>
    /// Projects every row exactly as it is typed, without the save-time validation. Read-only callers
    /// that only need to look at the configured providers — the Covenant retention disclosure resolves
    /// its help targets this way while a section is being constructed — must use this: a validating
    /// projection makes rendering a section throw on a value the operator has not asked to save yet,
    /// and a throw during view construction has no error surface to land on.
    /// </summary>
    public ProviderSettings[] BuildProvidersUnvalidated() =>
        [.. Providers.Select(static provider => provider.BuildUnvalidated())];

    [RelayCommand]
    private void AddProvider() =>
        Providers.Add(new ProviderViewModel(new ProviderSettings(), _dialogService, _probeClient));

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

        /// <summary>
        /// Optional path to the subscription CLI. Blank resolves the kind's default name on PATH.
        /// </summary>
        [ObservableProperty] private string _command = string.Empty;

        /// <summary>
        /// Comma-separated model ids left out of listings and pickers. Subtractive: empty means every
        /// model the CLI offers is available, which is how a newly released model becomes usable with
        /// no edit here. Hidden is not blocked — an explicitly named model still runs.
        /// </summary>
        [ObservableProperty] private string _hiddenModels = string.Empty;

        /// <summary>One line of readiness from the host probe, or why there is none.</summary>
        [ObservableProperty] private string _probeStatus = string.Empty;

        /// <summary>What the operator should do next; empty when there is nothing to do.</summary>
        [ObservableProperty] private string _probeRemediation = string.Empty;

        /// <summary>A command the operator runs themselves — Arcanum never signs in for them.</summary>
        [ObservableProperty] private string _probeRemediationCommand = string.Empty;

        [ObservableProperty] private bool _isProbing;

        public ObservableCollection<ModelEntryViewModel> Models { get; } = [];

        /// <summary>
        /// Whether this row describes a subscription CLI. Drives which fields the page shows: a
        /// Familiar has no endpoint and no credential reference, and rendering them blank-but-present
        /// would read as "required, and you have not filled them in".
        /// </summary>
        public bool IsFamiliar => FamiliarProviders.IsFamiliar(Type);

        public bool IsHttpProvider => !IsFamiliar;

        public bool HasProbeRemediation => !string.IsNullOrWhiteSpace(ProbeRemediation);

        public bool HasProbeRemediationCommand => !string.IsNullOrWhiteSpace(ProbeRemediationCommand);

        private ProviderSettings _snapshot;

        private readonly IDialogService _dialogService;

        private readonly IFamiliarProbeClient? _probeClient;

        public ProviderViewModel(
            ProviderSettings snapshot,
            IDialogService dialogService,
            IFamiliarProbeClient? probeClient = null)
        {

            _snapshot = snapshot;

            _dialogService = dialogService;

            _probeClient = probeClient;

            LoadFrom(snapshot);

        }

        partial void OnTypeChanged(AiProviderKind value)
        {

            _ = value;

            OnPropertyChanged(nameof(IsFamiliar));

            OnPropertyChanged(nameof(IsHttpProvider));

        }

        partial void OnProbeRemediationChanged(string value)
        {

            _ = value;

            OnPropertyChanged(nameof(HasProbeRemediation));

        }

        partial void OnProbeRemediationCommandChanged(string value)
        {

            _ = value;

            OnPropertyChanged(nameof(HasProbeRemediationCommand));

        }

        /// <summary>
        /// Asks the running host whether this Familiar is ready. A host that is not running is a
        /// state with an instruction, not a spinner and not an error dialog — and the hide list stays
        /// editable throughout, because none of it depends on the probe succeeding.
        /// </summary>
        [RelayCommand]
        private async Task ProbeAsync(CancellationToken cancellationToken)
        {

            if (_probeClient is null || !IsFamiliar)
            {

                return;

            }

            IsProbing = true;

            try
            {

                Result<FamiliarProbeResult> probed = await _probeClient
                    .ProbeAsync(Name, cancellationToken)
                    .ConfigureAwait(true);

                if (probed.IsFailure)
                {

                    ProbeStatus = probed.Error.Message;

                    ProbeRemediation = string.Empty;

                    ProbeRemediationCommand = string.Empty;

                    return;

                }

                FamiliarProbeResult result = probed.Value!;

                ProbeStatus = result.Version is { Length: > 0 } version
                    ? $"{result.Status}: {result.Summary} (version {version})"
                    : $"{result.Status}: {result.Summary}";

                ProbeRemediation = result.Remediation;

                ProbeRemediationCommand = result.RemediationCommand ?? string.Empty;

            }
            finally
            {

                IsProbing = false;

            }

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

            Command = snapshot.Command ?? string.Empty;

            HiddenModels = string.Join(", ", snapshot.HiddenModels ?? []);

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

            return BuildUnvalidated();
        }

        /// <summary>
        /// The same projection without the save-time validation, for callers that only read the row.
        /// </summary>
        public ProviderSettings BuildUnvalidated()
        {
            bool familiar = FamiliarProviders.IsFamiliar(Type);

            return _snapshot with
            {
                Name = Name,
                Type = Type,
                // A Familiar has no endpoint and no credential of its own — persisting a leftover
                // value from a row that used to be OpenAI-compatible would fail validation and imply
                // Arcanum holds a key it never reads.
                Endpoint = familiar ? string.Empty : Endpoint,
                CredentialEnvironmentVariable = familiar
                    ? null
                    : NullIfWhiteSpace(CredentialEnvironmentVariable),
                Command = familiar ? NullIfWhiteSpace(Command) : null,
                Models = [.. Models.Select(static model => model.Build())],
                HiddenModels = familiar ? SplitList(HiddenModels) : [],
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

            HasReasoning = snapshot.Reasoning?.WireDialect is not null;

            ReasoningDialect = snapshot.Reasoning?.WireDialect
                ?? ReasoningWireDialect.Standard;

            HasReasoningMaxBudgetTokens = snapshot.Reasoning?.MaxBudgetTokens is not null;

            ReasoningMaxBudgetTokens = snapshot.Reasoning?.MaxBudgetTokens ?? 1;

        }

        public ModelEntry Build() => _snapshot with
        {
            Name = Name,
            SupportsVision = SupportsVision,
            Reasoning = !HasReasoning
                ? null
                : new ModelReasoningSettings
                {
                    WireDialect = ReasoningDialect,
                    MaxBudgetTokens = HasReasoningMaxBudgetTokens
                        ? ReasoningMaxBudgetTokens
                        : null,
                },
        };

    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value;

    /// <summary>
    /// Splits the chips editor's comma-separated text, matching how every other
    /// <c>SettingKind.StringArray</c> field round-trips.
    /// </summary>
    private static string[] SplitList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

}