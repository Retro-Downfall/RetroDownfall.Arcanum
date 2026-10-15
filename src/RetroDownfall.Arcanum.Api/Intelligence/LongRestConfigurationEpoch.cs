using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Coherent process-owned snapshots. Observed transitions cannot be undone by value ABA.</summary>
public sealed class LongRestConfigurationEpoch : IDisposable, ILongRestConfigurationProvider
{
    private readonly object _gate = new();

    private readonly Guid _bootId;

    private readonly IDisposable? _subscription;

    private LongRestConfigurationSnapshot _current;

    public LongRestConfigurationEpoch(IOptionsMonitor<ArcanumSettings> options, Guid bootId)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (bootId == Guid.Empty)
        {
            throw new ArgumentException("A process boot identity is required.", nameof(bootId));
        }

        _bootId = bootId;

        lock (_gate)
        {
            _current = Snapshot(options.CurrentValue, 0);

            _subscription = options.OnChange((value, _) => Changed(value));

            // Cover a change between the initial read and subscription registration.
            Changed(options.CurrentValue);
        }
    }

    public LongRestConfigurationSnapshot Capture()
    {
        lock (_gate)
        {
            return _current;
        }
    }

    LongRestSweepConfiguration ILongRestConfigurationProvider.Capture()
    {
        LongRestConfigurationSnapshot snapshot = Capture();

        // The recipe is resolved by the same predicates the configuration validator, doctor and health rows use.
        Result<LongRestRecipeIdentity> recipe = snapshot.Enabled
            ? snapshot.ResolveRecipe()
            : Result<LongRestRecipeIdentity>.Failure(new Error(ErrorCodes.LongRest.Unavailable, "The exact Long Rest provider recipe is unavailable."));

        return new(snapshot.Stamp, snapshot.Enabled, snapshot.CovenantEnabled, recipe);
    }

    public bool IsCurrent(LongRestConfigurationStamp stamp)
    {
        lock (_gate)
        {
            return _current.Stamp == stamp;
        }
    }

    private void Changed(ArcanumSettings settings)
    {
        lock (_gate)
        {
            LongRestConfigurationSnapshot next = Snapshot(settings, _current.Stamp.Epoch);

            if (next.Stamp.ValueDigest != _current.Stamp.ValueDigest)
            {
                _current = Snapshot(settings, checked(_current.Stamp.Epoch + 1));
            }
        }
    }

    private LongRestConfigurationSnapshot Snapshot(ArcanumSettings settings, long epoch)
    {
        // Only configuration that controls this lane participates; serialize with the AOT context.
        ArcanumSettings relevant = new()
        {
            Features = new() { LongRest = settings.Features?.LongRest is true, Covenant = settings.Features?.Covenant is true },
            Providers = settings.Providers ?? [],
            DefaultModel = settings.DefaultModel,
            FastModel = settings.FastModel,
            Integrations = new() { Embeddings = settings.Integrations?.Embeddings ?? new() },
            Cost = new() { Pricing = settings.ResolvePricing() },
        };

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(relevant, ConfigurationJsonContext.Default.ArcanumSettings);

        CovenantDigest digest = new(SHA256.HashData(ArcanumCanonicalJsonV1.Canonicalize(bytes)));

        return new(new(_bootId, epoch, digest), bytes);
    }

    public void Dispose() => _subscription?.Dispose();
}

/// <summary>Private frozen bytes; callers receive fresh selections and cannot mutate the epoch.</summary>
public sealed class LongRestConfigurationSnapshot
{
    private readonly byte[] _settings;

    internal LongRestConfigurationSnapshot(LongRestConfigurationStamp stamp, byte[] settings)
    {
        Stamp = stamp;

        _settings = settings;
    }

    public LongRestConfigurationStamp Stamp { get; }

    private ArcanumSettings Read() => JsonSerializer.Deserialize(_settings, ConfigurationJsonContext.Default.ArcanumSettings)!;

    public bool Enabled => Read().Features.LongRest;

    public bool CovenantEnabled => Read().Features.Covenant;

    /// <summary>
    /// The chat choice. An explicit <paramref name="model"/> resolves exactly (an admitted choice does not drift); without one the
    /// fast model is used when it is set, else the default model, else the first advertised model. A fast model that is set but
    /// unusable fails closed.
    /// </summary>
    public Result<LongRestProviderSelection> ResolveChat(string? model = null)
    {
        ArcanumSettings settings = Read();

        return LongRestRecipeResolver.TryResolveChat(settings, model, out ProviderSettings? provider, out string resolved)
            ? Result<LongRestProviderSelection>.Success(new LongRestProviderSelection(provider!, resolved, 0, settings.ResolvePricing().ResolveForModel(resolved)))
            : Result<LongRestProviderSelection>.Failure(Invalid());
    }

    public Result<LongRestProviderSelection> ResolveEmbedding()
    {
        ArcanumSettings settings = Read();

        return LongRestRecipeResolver.TryResolveEmbedding(settings, out ProviderSettings? provider, out EmbeddingSettings embedding)
            ? Result<LongRestProviderSelection>.Success(new LongRestProviderSelection(provider!, embedding.Model!, embedding.Dimensions, settings.ResolvePricing().ResolveForModel(embedding.Model)))
            : Result<LongRestProviderSelection>.Failure(Invalid());
    }

    /// <summary>
    /// The recipe these frozen settings run: chat (fast model first), embedding and their destinations. A recipe that cannot resolve
    /// fails with <see cref="ErrorCodes.LongRest.Unavailable"/> and the one content-free sentence the configuration validator, the
    /// doctor row and the health component give (<see cref="LongRestRecipeResolver.Describe"/>), so a Session turn that ends on it says why.
    /// </summary>
    public Result<LongRestRecipeIdentity> ResolveRecipe()
    {
        ArcanumSettings settings = Read();

        Result<LongRestRecipeIdentity> recipe = LongRestRecipeResolver.Resolve(settings);

        return recipe.IsSuccess ? recipe : Result<LongRestRecipeIdentity>.Failure(
            new Error(ErrorCodes.LongRest.Unavailable, LongRestRecipeResolver.Describe(LongRestRecipeResolver.Problems(settings))));
    }

    private static Error Invalid() => new(ErrorCodes.LongRest.InvalidRequest, "No exact configured Long Rest provider model is available.");
}

public sealed record LongRestProviderSelection(ProviderSettings Provider, string Model, int Dimensions, ModelPricingEntry Pricing);
