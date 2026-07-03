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

        [ObservableProperty] private string _models = string.Empty;

        [ObservableProperty] private int _contextWindowLimit;

        [ObservableProperty] private string _llamaCppModelMap = string.Empty;

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

            Models = snapshot.Models.JoinCsv();

            ContextWindowLimit = snapshot.ContextWindowLimit;

            LlamaCppModelMap = snapshot.LlamaCpp?.ModelMap is not null
                ? string.Join(", ", snapshot.LlamaCpp.ModelMap.Select(static kvp => $"{kvp.Key}={kvp.Value}"))
                : string.Empty;

        }

        public ProviderSettings Build() => _snapshot with
        {

            Name = Name,

            Type = Type,

            Endpoint = Endpoint,

            ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey,

            Models = Models.SplitCsv(),

            ContextWindowLimit = ContextWindowLimit,

            LlamaCpp = ParseLlamaCppModelMap(),

        };

        private ProviderLlamaCppSettings? ParseLlamaCppModelMap()
        {

            Dictionary<string, string>? map = null;

            foreach (string entry in LlamaCppModelMap.SplitCsv())
            {

                int equals = entry.IndexOf('=', StringComparison.Ordinal);

                if (equals <= 0)
                {

                    continue;

                }

                map ??= [];

                map[entry[..equals].Trim()] = entry[(equals + 1)..].Trim();

            }

            return map is null ? null : new ProviderLlamaCppSettings { ModelMap = map };

        }

    }

}
