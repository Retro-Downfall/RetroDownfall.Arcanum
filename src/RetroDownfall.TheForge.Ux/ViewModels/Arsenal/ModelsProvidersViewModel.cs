using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Arsenal;

/// <summary>Models &amp; Providers tab of The Arsenal: read-only lists plus a provider connectivity test.</summary>
public sealed partial class ModelsProvidersViewModel : ViewModelBase
{

    private readonly IModelsProvidersDataSource _dataSource;

    private readonly FoundryFloorViewModel _foundryFloor;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string? _statusText;

    public ObservableCollection<ModelInfoDto> Models { get; } = [];

    public ObservableCollection<ProviderInfoDto> Providers { get; } = [];

    [ObservableProperty]
    private string _testEndpoint = string.Empty;

    [ObservableProperty]
    private string _testApiKey = string.Empty;

    [ObservableProperty]
    private string? _testResultText;

    [ObservableProperty]
    private bool? _testIsReachable;

    [ObservableProperty]
    private long? _testLatencyMs;

    public ModelsProvidersViewModel(IModelsProvidersDataSource dataSource, FoundryFloorViewModel foundryFloor)
    {

        _dataSource = dataSource;

        _foundryFloor = foundryFloor;

        Title = "Models & Providers";

    }

    public string TestNote => "Test connection — credentials are not stored.";

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            IReadOnlyList<ModelInfoDto> models = await _dataSource.ListModelsAsync(cancellationToken).ConfigureAwait(true);

            IReadOnlyList<ProviderInfoDto> providers = await _dataSource.ListProvidersAsync(cancellationToken).ConfigureAwait(true);

            Models.Clear();

            foreach (ModelInfoDto model in models)
            {

                Models.Add(model);

            }

            Providers.Clear();

            foreach (ProviderInfoDto provider in providers)
            {

                Providers.Add(provider);

            }

            StatusText = $"{models.Count} model(s), {providers.Count} provider(s).";

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Models/Providers refresh error: {ex.Message}");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task TestProviderAsync(CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(TestEndpoint))
        {

            StatusText = "Enter an endpoint to test.";

            return;

        }

        IsBusy = true;

        LastError = null;

        TestResultText = null;

        TestIsReachable = null;

        TestLatencyMs = null;

        try
        {

            ProviderTestRequest request = new(TestEndpoint, string.IsNullOrWhiteSpace(TestApiKey) ? null : TestApiKey, AiProviderKind.OpenAICompatible);

            ProviderTestResult? result = await _dataSource.TestProviderAsync(request, cancellationToken).ConfigureAwait(true);

            if (result is { } r)
            {

                TestIsReachable = r.IsReachable;

                TestLatencyMs = r.LatencyMs;

                TestResultText = r.IsReachable
                    ? $"Reachable in {r.LatencyMs} ms — {r.ModelsFound.Length} model(s)."
                    : $"Unreachable: {r.Error ?? "unknown error"}";

                StatusText = r.IsReachable ? "Provider reachable." : "Provider unreachable.";

            }
            else
            {

                TestResultText = "Test failed.";

                StatusText = "Provider test failed.";

                LastError = "Provider test failed.";

                _foundryFloor.AppendLine("Models/Providers provider test failed.");

            }

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Models/Providers test error: {ex.Message}");

        }
        finally
        {

            IsBusy = false;

        }

    }

}
