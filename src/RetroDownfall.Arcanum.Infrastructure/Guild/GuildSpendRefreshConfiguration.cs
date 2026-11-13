using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Private in-process proof of one effective provider publication, never persisted as source authority.</summary>
internal sealed class GuildSpendRefreshConfiguration : IDisposable
{
    private readonly object _gate = new();

    private readonly IOptionsMonitor<ArcanumSettings> _options;

    private readonly IDisposable? _subscription;

    private long _epoch;

    private int _disposed;

    internal GuildSpendRefreshConfiguration(IOptionsMonitor<ArcanumSettings> options)
    {
        _options = options;

        _subscription = options.OnChange((_, _) => Changed());
    }

    internal Result<GuildSpendRefreshConfigurationSnapshot> Capture(string provider, string model)
    {
        if (!Monitor.TryEnter(_gate)) return Unavailable;

        try
        {
            long epoch = Volatile.Read(ref _epoch);

            if (Volatile.Read(ref _disposed) != 0 || epoch == long.MaxValue) return Unavailable;

            ArcanumSettings publication = _options.CurrentValue;

            if (!TrySelection(publication, provider, model, out byte[]? selection, out byte[]? pricing)
                || epoch != Volatile.Read(ref _epoch) || !ReferenceEquals(publication, _options.CurrentValue)) return Unavailable;

            return new GuildSpendRefreshConfigurationSnapshot(this, publication, epoch, provider, model, selection!, pricing!);
        }
        catch (Exception error) when (error is InvalidOperationException or OptionsValidationException)
        {
            return Unavailable;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    internal bool IsExplicitlyDisabled(string providerName)
    {
        if (!Monitor.TryEnter(_gate)) return false;

        try
        {
            long epoch = Volatile.Read(ref _epoch);

            if (Volatile.Read(ref _disposed) != 0 || epoch == long.MaxValue) return false;

            ArcanumSettings publication = _options.CurrentValue;

            if (publication is null) return false;

            ProviderSettings[] matches = publication.Providers?.Where(provider => provider is not null && provider.Name == providerName).ToArray() ?? [];

            return matches.Length == 1 && matches[0].GuildUsageReconciliation == GuildUsageReconciliationSource.None
                && epoch == Volatile.Read(ref _epoch) && ReferenceEquals(publication, _options.CurrentValue)
                && matches[0].GuildUsageReconciliation == GuildUsageReconciliationSource.None;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    internal bool IsCurrent(GuildSpendRefreshConfigurationSnapshot snapshot)
    {
        if (snapshot is null || !ReferenceEquals(snapshot.Owner, this) || !Monitor.TryEnter(_gate)) return false;

        try
        {
            if (Volatile.Read(ref _disposed) != 0 || snapshot.Epoch == long.MaxValue || snapshot.Epoch != Volatile.Read(ref _epoch)) return false;

            ArcanumSettings publication = _options.CurrentValue;

            return ReferenceEquals(snapshot.Publication, publication)
                && TrySelection(publication, snapshot.ProviderName, snapshot.Model, out byte[]? selection, out byte[]? pricing)
                && snapshot.Matches(selection!, pricing!) && snapshot.Epoch == Volatile.Read(ref _epoch)
                && ReferenceEquals(publication, _options.CurrentValue);
        }
        catch (Exception error) when (error is InvalidOperationException or OptionsValidationException)
        {
            return false;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private static bool TrySelection(ArcanumSettings settings, string providerName, string model,
        out byte[]? selection, out byte[]? pricing)
    {
        selection = null;

        pricing = null;

        if (settings is null || string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(model)
            || settings.Providers is null || settings.Cost?.Pricing is null) return false;

        ProviderSettings[] matches = settings.Providers.Where(provider => provider is not null && provider.Name == providerName).ToArray();

        if (matches.Length != 1) return false;

        ProviderSettings chosen = matches[0];

        if (chosen.Type != AiProviderKind.OpenAICompatible || chosen.GuildUsageReconciliation != GuildUsageReconciliationSource.OpenRouterGeneration
            || !ProviderSettings.IsCanonicalOpenRouterEndpoint(chosen.Endpoint) || chosen.Models is null
            || !chosen.Models.Any(entry => entry is not null && entry.Name == model)) return false;

        try
        {
            selection = JsonSerializer.SerializeToUtf8Bytes(chosen, ConfigurationJsonContext.Default.ProviderSettings);

            pricing = JsonSerializer.SerializeToUtf8Bytes(settings.Cost.Pricing.ResolveForModel(model), ConfigurationJsonContext.Default.ModelPricingEntry);

            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private void Changed()
    {
        while (true)
        {
            long current = Volatile.Read(ref _epoch);

            if (current == long.MaxValue || Interlocked.CompareExchange(ref _epoch, current + 1, current) == current) return;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);

        _subscription?.Dispose();
    }

    private static Error Unavailable => new(ErrorCodes.Guild.Unavailable, "The provider refresh configuration is unavailable or changed.");
}

internal sealed class GuildSpendRefreshConfigurationSnapshot(
    GuildSpendRefreshConfiguration owner, ArcanumSettings publication, long epoch, string providerName, string model,
    byte[] selection, byte[] pricing)
{
    private readonly byte[] _selection = (byte[])selection.Clone();

    private readonly byte[] _pricing = (byte[])pricing.Clone();

    internal GuildSpendRefreshConfiguration Owner { get; } = owner;

    internal ArcanumSettings Publication { get; } = publication;

    internal long Epoch { get; } = epoch;

    internal string ProviderName { get; } = providerName;

    internal string Model { get; } = model;

    internal bool Matches(byte[] selection, byte[] pricing) => _selection.AsSpan().SequenceEqual(selection)
        && _pricing.AsSpan().SequenceEqual(pricing);

    internal ProviderSettings ReadProvider() => JsonSerializer.Deserialize(_selection, ConfigurationJsonContext.Default.ProviderSettings)!;
}

/// <summary>Ephemeral post-I/O credential and publication proof; no secret or stamp is persisted.</summary>
internal sealed class GuildSpendRefreshCurrentness
{
    private readonly GuildSpendRefreshConfiguration _configuration;

    private readonly GuildSpendRefreshConfigurationSnapshot _snapshot;

    private readonly RetroDownfall.Arcanum.Core.Security.IProviderApiKeyResolver _resolver;

    private readonly ProviderSettings _provider;

    private readonly RetroDownfall.Arcanum.Core.Security.ProviderCredentialStamp _stamp;

    private GuildSpendRefreshCurrentness(GuildSpendRefreshConfiguration configuration, GuildSpendRefreshConfigurationSnapshot snapshot,
        RetroDownfall.Arcanum.Core.Security.IProviderApiKeyResolver resolver, ProviderSettings provider,
        RetroDownfall.Arcanum.Core.Security.ProviderCredentialStamp stamp, GuildSpendSourceCredential credential)
    {
        _configuration = configuration;

        _snapshot = snapshot;

        _resolver = resolver;

        _provider = provider;

        _stamp = stamp;

        Credential = credential;
    }

    internal GuildSpendSourceCredential Credential { get; }

    internal static async Task<Result<GuildSpendRefreshCurrentness>> CaptureAsync(GuildSpendRefreshConfiguration configuration,
        RetroDownfall.Arcanum.Core.Security.IProviderApiKeyResolver resolver, string providerName, string model, CancellationToken ct)
    {
        Result<GuildSpendRefreshConfigurationSnapshot> selected = configuration.Capture(providerName, model);

        if (selected.IsFailure) return selected.Error;

        ProviderSettings provider = selected.Value.ReadProvider();

        RetroDownfall.Arcanum.Core.Security.ResolvedProviderCredential observed = await resolver.PeekWithIdentityAsync(provider, ct).ConfigureAwait(false);

        if (observed.Stamp is null || string.IsNullOrEmpty(observed.Secret) || !configuration.IsCurrent(selected.Value)) return Unavailable;

        RetroDownfall.Arcanum.Core.Security.ProviderCredentialCurrentness current = await resolver.GetCurrentnessAsync(provider, observed.Stamp, ct).ConfigureAwait(false);

        if (current != RetroDownfall.Arcanum.Core.Security.ProviderCredentialCurrentness.Current || !configuration.IsCurrent(selected.Value)) return Unavailable;

        return new GuildSpendRefreshCurrentness(configuration, selected.Value, resolver, provider, observed.Stamp,
            new GuildSpendSourceCredential(provider, model, observed.Secret));
    }

    internal bool IsCurrent() =>
        _resolver.GetObservedLocalCurrentness(_provider, _stamp) == RetroDownfall.Arcanum.Core.Security.ProviderCredentialCurrentness.Current
        && _configuration.IsCurrent(_snapshot)
        && _resolver.GetObservedLocalCurrentness(_provider, _stamp) == RetroDownfall.Arcanum.Core.Security.ProviderCredentialCurrentness.Current;

    private static Error Unavailable => new(ErrorCodes.Guild.Unavailable, "The provider refresh currentness proof is unavailable or changed.");
}
