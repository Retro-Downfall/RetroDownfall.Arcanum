using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;

/// <summary>Process-owned immutable configuration snapshots; observed ABA never revives a plan.</summary>
public sealed class MemoryEvaluationProviderEpoch : IDisposable
{
    private readonly object _gate = new();

    private readonly IDisposable? _subscription;

    private MemoryEvaluationProviderSnapshot _current;

    public MemoryEvaluationProviderEpoch(IOptionsMonitor<ArcanumSettings> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_gate)
        {
            _current = Snapshot(options.CurrentValue, 0);

            _subscription = options.OnChange((settings, _) => Changed(settings));

            Changed(options.CurrentValue);
        }
    }

    public MemoryEvaluationProviderSnapshot Capture()
    {
        lock (_gate) return _current;
    }

    public bool IsCurrent(MemoryEvaluationProviderSnapshot snapshot)
    {
        lock (_gate) return ReferenceEquals(snapshot, _current);
    }

    public void Dispose() => _subscription?.Dispose();

    private void Changed(ArcanumSettings settings)
    {
        lock (_gate)
        {
            var next = Snapshot(settings, checked(_current.Generation + 1));

            if (next.Digest != _current.Digest) _current = next;
        }
    }

    private static MemoryEvaluationProviderSnapshot Snapshot(ArcanumSettings settings, long generation)
    {
        ArcanumSettings relevant = new()
        {
            Providers = settings.Providers,
            Features = new() { Reasoning = settings.Features.Reasoning, Covenant = settings.Features.Covenant },
            Cost = settings.Cost,
        };

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(relevant, ConfigurationJsonContext.Default.ArcanumSettings);

        return new(generation, bytes, new(SHA256.HashData(bytes)));
    }
}

public sealed class MemoryEvaluationProviderSnapshot
{
    private readonly byte[] _settings;

    internal MemoryEvaluationProviderSnapshot(long generation, byte[] settings, CovenantDigest digest)
    {
        Generation = generation;

        _settings = settings;

        Digest = digest;
    }

    public long Generation { get; }

    public CovenantDigest Digest { get; }

    public ArcanumSettings Read() => JsonSerializer.Deserialize(_settings, ConfigurationJsonContext.Default.ArcanumSettings)!;
}
