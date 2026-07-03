using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class LlamaCppSectionViewModel : ObservableObject
{

    [ObservableProperty] private string _serverExecutablePath = string.Empty;

    [ObservableProperty] private int _gpuLayers;

    [ObservableProperty] private int _contextSize;

    [ObservableProperty] private int _portStart;

    [ObservableProperty] private int _portRange;

    [ObservableProperty] private int _maxConcurrentRequests;

    [ObservableProperty] private int _healthProbeTimeoutSeconds;

    [ObservableProperty] private int _startTimeoutSeconds;

    [ObservableProperty] private int _shutdownTimeoutSeconds;

    [ObservableProperty] private string _additionalArguments = string.Empty;

    [ObservableProperty] private int _maxCachedModels;

    [ObservableProperty] private int _modelDownloadTimeoutSeconds;

    [ObservableProperty] private long _modelDownloadMaxBytes;

    [ObservableProperty] private string _modelSha256Map = string.Empty;

    [ObservableProperty] private bool _requireModelHash;

    private LlamaCppSettings _snapshot = new();

    public void LoadFrom(LlamaCppSettings settings)
    {

        _snapshot = settings;

        ServerExecutablePath = settings.ServerExecutablePath ?? string.Empty;

        GpuLayers = settings.GpuLayers;

        ContextSize = settings.ContextSize;

        PortStart = settings.PortStart;

        PortRange = settings.PortRange;

        MaxConcurrentRequests = settings.MaxConcurrentRequests;

        HealthProbeTimeoutSeconds = settings.HealthProbeTimeoutSeconds;

        StartTimeoutSeconds = settings.StartTimeoutSeconds;

        ShutdownTimeoutSeconds = settings.ShutdownTimeoutSeconds;

        AdditionalArguments = settings.AdditionalArguments is not null
            ? string.Join(" ", settings.AdditionalArguments)
            : string.Empty;

        MaxCachedModels = settings.MaxCachedModels;

        ModelDownloadTimeoutSeconds = settings.ModelDownloadTimeoutSeconds;

        ModelDownloadMaxBytes = settings.ModelDownloadMaxBytes;

        ModelSha256Map = settings.ModelSha256Map is not null
            ? string.Join(", ", settings.ModelSha256Map.Select(static kvp => $"{kvp.Key}={kvp.Value}"))
            : string.Empty;

        RequireModelHash = settings.RequireModelHash;

    }

    public LlamaCppSettings Build() => _snapshot with
    {

        ServerExecutablePath = string.IsNullOrWhiteSpace(ServerExecutablePath) ? null : ServerExecutablePath,

        GpuLayers = GpuLayers,

        ContextSize = ContextSize,

        PortStart = PortStart,

        PortRange = PortRange,

        MaxConcurrentRequests = MaxConcurrentRequests,

        HealthProbeTimeoutSeconds = HealthProbeTimeoutSeconds,

        StartTimeoutSeconds = StartTimeoutSeconds,

        ShutdownTimeoutSeconds = ShutdownTimeoutSeconds,

        AdditionalArguments = string.IsNullOrWhiteSpace(AdditionalArguments)
            ? null
            : AdditionalArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries),

        MaxCachedModels = MaxCachedModels,

        ModelDownloadTimeoutSeconds = ModelDownloadTimeoutSeconds,

        ModelDownloadMaxBytes = ModelDownloadMaxBytes,

        ModelSha256Map = ParseSha256Map(),

        RequireModelHash = RequireModelHash,

    };

    private Dictionary<string, string>? ParseSha256Map()
    {

        Dictionary<string, string>? map = null;

        foreach (string entry in ModelSha256Map.SplitCsv())
        {

            int equals = entry.IndexOf('=', StringComparison.Ordinal);

            if (equals <= 0)
            {

                continue;

            }

            map ??= [];

            map[entry[..equals].Trim()] = entry[(equals + 1)..].Trim();

        }

        return map;

    }

}
