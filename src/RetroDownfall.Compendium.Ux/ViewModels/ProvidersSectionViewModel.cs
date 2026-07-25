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

    private ProviderSettings[] _providerSnapshot = [];

    public void LoadFrom(
        ProviderSettings[] providers,
        string? defaultModel,
        string? fastModel)
    {

        _providerSnapshot = providers;

        DefaultModel = defaultModel ?? string.Empty;

        FastModel = fastModel ?? string.Empty;

        Providers.Clear();

        foreach (ProviderSettings provider in providers)
        {

            Providers.Add(new ProviderViewModel(provider));

        }

    }

    public ProviderSettings[] BuildProviders()
    {

        if (Providers.Count == 0)
        {

            return [];

        }

        return Providers.Select(static p => p.Build()).ToArray();

    }

    [RelayCommand]
    private void AddProvider()
    {

        Providers.Add(new ProviderViewModel(new ProviderSettings()));

    }

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

        [ObservableProperty] private string _apiKey = string.Empty;

        [ObservableProperty] private int _contextWindowLimit;

        /// <summary>
        /// Editable per-model name and Scrying rows. Each row also retains its optional reasoning
        /// capability object as opaque metadata so ordinary provider edits round-trip it unchanged;
        /// the polished provider page is not a full reasoning-capability editor.
        /// </summary>
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

            ApiKey = snapshot.ApiKey ?? string.Empty;

            Models.Clear();

            foreach (ModelEntry model in snapshot.Models)
            {

                Models.Add(new ModelEntryViewModel(model));

            }

            ContextWindowLimit = snapshot.ContextWindowLimit;

        }

        public ProviderSettings Build() => _snapshot with
        {

            Name = Name,

            Type = Type,

            Endpoint = Endpoint,

            ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey,

            Models = [.. Models.Select(static m => m.Build())],

            ContextWindowLimit = ContextWindowLimit,

        };

        [RelayCommand]
        private void AddModel()
        {

            Models.Add(new ModelEntryViewModel());

        }

        [RelayCommand]
        private void RemoveModel(ModelEntryViewModel? model)
        {

            if (model is not null)
            {

                Models.Remove(model);

            }

        }

    }

    /// <summary>
    /// A single <c>Arcanum:Providers[].models</c> entry. Name and Scrying support are edited by the
    /// polished provider UI; optional reasoning metadata is preserved unchanged for configuration
    /// or generic-descriptor editing. See <see cref="ModelEntry"/>.
    /// </summary>
    public sealed partial class ModelEntryViewModel : ObservableObject
    {

        [ObservableProperty] private string _name = string.Empty;

        [ObservableProperty] private bool _supportsVision;

        [ObservableProperty] private ReasoningCapabilities? _reasoning;

        public ModelEntryViewModel()
        {
        }

        public ModelEntryViewModel(ModelEntry entry)
        {

            Name = entry.Name;

            SupportsVision = entry.SupportsVision;

            Reasoning = entry.Reasoning;

        }

        public ModelEntry Build() => new(Name, SupportsVision, Reasoning);

    }

}
