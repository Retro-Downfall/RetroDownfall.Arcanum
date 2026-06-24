using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Api.Configuration;

internal static class ConfigurationRedactor
{

    public static ArcanumSettings Redact(ArcanumSettings settings)
    {
        ProviderSettings[] providers = settings.Providers ?? [];

        ProviderSettings[] redactedProviders = providers
            .Select(static p => p with
            {
                ApiKey = Mask(p.ApiKey),
                Endpoint = MaskRequired(p.Endpoint),
                LlamaCpp = MaskLlamaCpp(p.LlamaCpp),
            })
            .ToArray();

        CommLinkSettings commLink = settings.CommLink with
        {
            WebhookUrl = Mask(settings.CommLink.WebhookUrl),
        };

        return Clone(settings) with { Providers = redactedProviders, CommLink = commLink };
    }

    public static ArcanumSettings MergeApiKeys(ArcanumSettings request, ArcanumSettings current)
    {
        ProviderSettings[] requestProviders = request.Providers ?? [];

        ProviderSettings[] currentProviders = current.Providers ?? [];

        Dictionary<string, ProviderSettings> currentByName = currentProviders
            .ToDictionary(static p => p.Name, static p => p, StringComparer.OrdinalIgnoreCase);

        ProviderSettings[] mergedProviders = requestProviders
            .Select(p => currentByName.TryGetValue(p.Name, out ProviderSettings? currentProvider)
                ? p with
                {
                    ApiKey = RestoreMask(p.ApiKey, currentProvider.ApiKey),
                    Endpoint = RestoreMaskRequired(p.Endpoint, currentProvider.Endpoint),
                    LlamaCpp = MergeLlamaCpp(p.LlamaCpp, currentProvider.LlamaCpp),
                }
                : p)
            .ToArray();

        CommLinkSettings mergedCommLink = request.CommLink with
        {
            WebhookUrl = RestoreMask(request.CommLink.WebhookUrl, current.CommLink.WebhookUrl),
        };

        return Clone(request) with { Providers = mergedProviders, CommLink = mergedCommLink };
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

    private static string? Mask(string? value) =>
        string.IsNullOrEmpty(value) ? value : "***";

    private static string MaskRequired(string value) =>
        string.IsNullOrEmpty(value) ? value : "***";

    private static string? RestoreMask(string? incoming, string? current) =>
        incoming == "***" ? current : incoming;

    private static string RestoreMaskRequired(string incoming, string current) =>
        incoming == "***" ? current : incoming;

    private static ProviderLlamaCppSettings? MaskLlamaCpp(ProviderLlamaCppSettings? llamaCpp)
    {
        if (llamaCpp?.ModelMap is not { Count: > 0 } modelMap)
        {
            return llamaCpp;
        }

        Dictionary<string, string> redactedMap = modelMap
            .ToDictionary(static pair => pair.Key, static _ => "***", StringComparer.OrdinalIgnoreCase);

        return llamaCpp with { ModelMap = redactedMap };
    }

    private static ProviderLlamaCppSettings? MergeLlamaCpp(
        ProviderLlamaCppSettings? incoming,
        ProviderLlamaCppSettings? current)
    {
        if (incoming?.ModelMap is not { Count: > 0 } requestMap)
        {
            return incoming;
        }

        Dictionary<string, string> currentMap = current?.ModelMap
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string> mergedMap = requestMap
            .ToDictionary(
                static pair => pair.Key,
                pair => pair.Value == "***" && currentMap.TryGetValue(pair.Key, out string? url)
                    ? url
                    : pair.Value,
                StringComparer.OrdinalIgnoreCase);

        return incoming with { ModelMap = mergedMap };
    }

}
