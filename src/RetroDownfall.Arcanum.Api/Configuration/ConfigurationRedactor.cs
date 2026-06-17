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

        Dictionary<string, string?> currentKeys = currentProviders
            .ToDictionary(static p => p.Name, static p => p.ApiKey, StringComparer.OrdinalIgnoreCase);

        ProviderSettings[] mergedProviders = requestProviders
            .Select(p => p with
            {
                ApiKey = p.ApiKey == "***" && currentKeys.TryGetValue(p.Name, out string? key)
                    ? key
                    : p.ApiKey,
            })
            .ToArray();

        return Clone(request) with { Providers = mergedProviders };
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

}
