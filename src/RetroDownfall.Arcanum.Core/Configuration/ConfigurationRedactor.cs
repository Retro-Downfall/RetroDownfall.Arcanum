using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

public static class ConfigurationRedactor
{

    private const string MaskSentinel = "***";

    public static ArcanumSettings Redact(ArcanumSettings settings)
    {
        ProviderSettings[] providers = settings.Providers ?? [];

        ProviderSettings[] redactedProviders = providers
            .Select(static p => RedactProvider(p))
            .ToArray();

        return Clone(settings) with
        {
            Providers = redactedProviders,
        };
    }

    public static ArcanumSettings MergeRedactedSecrets(ArcanumSettings request, ArcanumSettings current)
    {
        ProviderSettings[] requestProviders = request.Providers ?? [];

        ProviderSettings[] currentProviders = current.Providers ?? [];

        Dictionary<string, ProviderSettings> currentByName = new(StringComparer.OrdinalIgnoreCase);

        foreach (ProviderSettings? persisted in currentProviders)
        {
            ProviderSettings currentProvider = Normalize(persisted);

            if (string.IsNullOrWhiteSpace(currentProvider.Name))
            {
                continue;
            }

            // A hand-edited arcanum.json can carry the same provider name twice, and nothing validates
            // the persisted file semantically before it reaches this merge. First-wins keeps the merge
            // total so ConfigurationValidator reports "configured more than once" with a pointer instead
            // of ToDictionary throwing on the duplicate key.
            currentByName.TryAdd(currentProvider.Name, currentProvider);
        }

        ProviderSettings[] mergedProviders = requestProviders
            .Select(p => MergeProvider(Normalize(p), currentByName))
            .ToArray();

        return Clone(request) with
        {
            Providers = mergedProviders,
        };
    }

    // After MergeRedactedSecrets, a provider endpoint still equal to "***" belongs to a new provider
    // that could not be matched to the current configuration. Reject the literal mask.
    public static Result ValidateNoResidualMask(ArcanumSettings merged)
    {

        ProviderSettings[] providers = merged.Providers ?? [];

        foreach (ProviderSettings? entry in providers)
        {

            ProviderSettings provider = Normalize(entry);

            if (provider.Endpoint == MaskSentinel)
            {

                return Result.Failure(new Error(
                    "Config.UnresolvedMask",
                    $"Provider '{provider.Name}' has a masked endpoint ('{MaskSentinel}'); supply the real endpoint when adding a new provider."));

            }

        }

        return Result.Success();

    }

    private static ArcanumSettings Clone(ArcanumSettings source)
    {
        byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(
            new ArcanumConfigurationFile { Arcanum = source },
            ConfigurationJsonContext.Default.ArcanumConfigurationFile);

        ArcanumConfigurationFile? wrapper = JsonSerializer.Deserialize(
            utf8,
            ConfigurationJsonContext.Default.ArcanumConfigurationFile);

        return wrapper?.Arcanum ?? source;
    }

    // System.Text.Json writes a null element for a JSON null inside "providers", and a null value for an
    // explicit "name": null, however the POCO declares them. Normalizing to an empty provider keeps the
    // array index aligned with the file, so ConfigurationValidator can answer with "providers[i].name"
    // rather than every caller of this class throwing first.
    private static ProviderSettings Normalize(ProviderSettings? provider) =>
        provider ?? new ProviderSettings();

    private static ProviderSettings RedactProvider(ProviderSettings? provider)
    {
        ProviderSettings normalized = Normalize(provider);

        return normalized with
        {
            Endpoint = MaskRequired(normalized.Endpoint),
        };
    }

    private static ProviderSettings MergeProvider(
        ProviderSettings provider,
        Dictionary<string, ProviderSettings> currentByName)
    {
        if (string.IsNullOrWhiteSpace(provider.Name)
            || !currentByName.TryGetValue(provider.Name, out ProviderSettings? currentProvider))
        {
            return provider;
        }

        return provider with
        {
            Endpoint = RestoreMaskRequired(provider.Endpoint, currentProvider.Endpoint),
        };
    }

    private static string MaskRequired(string value) =>
        string.IsNullOrEmpty(value) ? value : "***";

    private static string RestoreMaskRequired(string incoming, string current) =>
        incoming == "***" ? current : incoming;
}
