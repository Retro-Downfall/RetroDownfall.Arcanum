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
            .Select(static p => p with
            {
                Endpoint = MaskRequired(p.Endpoint),
            })
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

        Dictionary<string, ProviderSettings> currentByName = currentProviders
            .ToDictionary(static p => p.Name, static p => p, StringComparer.OrdinalIgnoreCase);

        ProviderSettings[] mergedProviders = requestProviders
            .Select(p => currentByName.TryGetValue(p.Name, out ProviderSettings? currentProvider)
                ? p with
                {
                    Endpoint = RestoreMaskRequired(p.Endpoint, currentProvider.Endpoint),
                }
                : p)
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

        foreach (ProviderSettings provider in providers)
        {

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

    private static string MaskRequired(string value) =>
        string.IsNullOrEmpty(value) ? value : "***";

    private static string RestoreMaskRequired(string incoming, string current) =>
        incoming == "***" ? current : incoming;
}
