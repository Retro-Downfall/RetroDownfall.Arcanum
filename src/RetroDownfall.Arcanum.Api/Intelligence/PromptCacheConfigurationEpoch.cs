using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed record PromptCacheConfigurationStamp(Guid BootId, long Epoch, string ValueDigest);

/// <summary>
/// Process-owned observed configuration epochs. Notifications preserve observed A-B-A changes;
/// Capture and IsCurrent also observe in-place mutations. Unobserved external ABA is unknowable.
/// </summary>
public sealed class PromptCacheConfigurationEpoch : IDisposable
{
    private readonly object _gate = new();

    private readonly IOptionsMonitor<ArcanumSettings> _options;

    private readonly Guid _bootId;

    private readonly IDisposable? _subscription;

    private PromptCacheConfigurationSnapshot _current;

    public PromptCacheConfigurationEpoch(IOptionsMonitor<ArcanumSettings> options, Guid bootId)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (bootId == Guid.Empty)
        {
            throw new ArgumentException("A process boot identity is required.", nameof(bootId));
        }

        _options = options;

        _bootId = bootId;

        lock (_gate)
        {
            _current = Snapshot(options.CurrentValue, 0);

            _subscription = options.OnChange((value, _) => Changed(value));

            Changed(options.CurrentValue);
        }
    }

    public PromptCacheConfigurationSnapshot Capture()
    {
        lock (_gate)
        {
            Changed(_options.CurrentValue);

            return _current;
        }
    }

    public bool IsCurrent(PromptCacheConfigurationStamp stamp) => Capture().Stamp == stamp;

    private void Changed(ArcanumSettings settings)
    {
        lock (_gate)
        {
            PromptCacheConfigurationSnapshot next = Snapshot(settings, _current.Stamp.Epoch);

            if (next.Stamp.ValueDigest != _current.Stamp.ValueDigest)
            {
                _current = next.WithEpoch(checked(_current.Stamp.Epoch + 1));
            }
        }
    }

    private PromptCacheConfigurationSnapshot Snapshot(ArcanumSettings settings, long epoch)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(settings, ConfigurationJsonContext.Default.ArcanumSettings);

        string digest = Convert.ToHexString(SHA256.HashData(ArcanumCanonicalJsonV1.Canonicalize(bytes)));

        return new(new(_bootId, epoch, digest), bytes);
    }

    public void Dispose() => _subscription?.Dispose();
}

/// <summary>Private frozen settings bytes; every selection receives a fresh deep copy.</summary>
public sealed class PromptCacheConfigurationSnapshot
{
    private readonly byte[] _settings;

    internal PromptCacheConfigurationSnapshot(PromptCacheConfigurationStamp stamp, byte[] settings)
    {
        Stamp = stamp;

        _settings = settings;
    }

    public PromptCacheConfigurationStamp Stamp { get; }

    public ArcanumSettings ReadSettings() => JsonSerializer.Deserialize(_settings, ConfigurationJsonContext.Default.ArcanumSettings)!;

    internal PromptCacheConfigurationSnapshot WithEpoch(long epoch) => new(Stamp with { Epoch = epoch }, _settings);
}
