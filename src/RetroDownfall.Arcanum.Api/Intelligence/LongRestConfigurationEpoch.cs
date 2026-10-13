using System.Security.Cryptography;
using System.Text;
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

        Result<LongRestRecipeIdentity> recipe = Result<LongRestRecipeIdentity>.Failure(new Error(ErrorCodes.LongRest.Unavailable, "The exact Long Rest provider recipe is unavailable."));

        if (snapshot.Enabled)
        {
            Result<LongRestProviderSelection> chat = snapshot.ResolveChat();

            Result<LongRestProviderSelection> embedding = snapshot.ResolveEmbedding();

            if (chat.IsSuccess && embedding.IsSuccess && Supported(chat.Value) && Supported(embedding.Value))
            {
                recipe = Result<LongRestRecipeIdentity>.Success(new LongRestRecipeIdentity(1, 1, chat.Value.Provider.Name, chat.Value.Model, Destination(chat.Value.Provider),
                    embedding.Value.Provider.Name, embedding.Value.Model, Destination(embedding.Value.Provider), embedding.Value.Dimensions));

                if (LongRestDiscoveryHash.Recipe(recipe.Value).IsFailure)
                {
                    recipe = Result<LongRestRecipeIdentity>.Failure(new Error(ErrorCodes.LongRest.InvalidRequest, "The configured Long Rest recipe exceeds its supported bounds."));
                }
            }
        }

        return new(snapshot.Stamp, snapshot.Enabled, snapshot.CovenantEnabled, recipe);
    }

    private static bool Supported(LongRestProviderSelection selection) => selection.Provider.Type == AiProviderKind.OpenAICompatible
        && Uri.TryCreate(selection.Provider.Endpoint, UriKind.Absolute, out Uri? endpoint)
        && endpoint.Scheme is "https" or "http";

    private static CovenantDigest Destination(ProviderSettings provider) => new(SHA256.HashData(Encoding.UTF8.GetBytes(
        "Arcanum.Covenant.ProviderDestinationIdentity.v1\0" + provider.Name + "|" + provider.Endpoint)));

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

    public Result<LongRestProviderSelection> ResolveChat(string? model = null)
    {
        ArcanumSettings settings = Read();

        return ProviderResolver.TryResolveProviderForModel(settings, model, out ProviderSettings? provider, out string resolved)
            && provider is not null
            ? Result<LongRestProviderSelection>.Success(new LongRestProviderSelection(provider, resolved, 0, settings.ResolvePricing().ResolveForModel(resolved)))
            : Result<LongRestProviderSelection>.Failure(Invalid());
    }

    public Result<LongRestProviderSelection> ResolveEmbedding()
    {
        ArcanumSettings settings = Read();

        EmbeddingSettings embedding = settings.ResolveEmbeddings();

        return embedding.Dimensions > 0 && !string.IsNullOrWhiteSpace(embedding.Provider) && !string.IsNullOrWhiteSpace(embedding.Model)
            && ProviderResolver.TryResolveProviderByName(settings, embedding.Provider, out ProviderSettings? provider)
            && provider is not null
            ? Result<LongRestProviderSelection>.Success(new LongRestProviderSelection(provider, embedding.Model, embedding.Dimensions, settings.ResolvePricing().ResolveForModel(embedding.Model)))
            : Result<LongRestProviderSelection>.Failure(Invalid());
    }

    private static Error Invalid() => new(ErrorCodes.LongRest.InvalidRequest, "No exact configured Long Rest provider model is available.");
}

public sealed record LongRestProviderSelection(ProviderSettings Provider, string Model, int Dimensions, ModelPricingEntry Pricing);
