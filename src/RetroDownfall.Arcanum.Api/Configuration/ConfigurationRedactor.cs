using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Api.Configuration;

internal static class ConfigurationRedactor
{

    private const string MaskSentinel = "***";

    public static ArcanumSettings Redact(ArcanumSettings settings)
    {
        ProviderSettings[] providers = settings.Providers ?? [];

        ProviderSettings[] redactedProviders = providers
            .Select(static p => p with
            {
                ApiKey = Mask(p.ApiKey),
                Endpoint = MaskRequired(p.Endpoint),
            })
            .ToArray();

        CommLinkSettings commLink = settings.CommLink with
        {
            WebhookUrl = Mask(settings.CommLink.WebhookUrl),
        };

        HostSettings host = settings.Host with
        {
            Https = settings.Host.Https with
            {
                CertificatePassword = Mask(settings.Host.Https.CertificatePassword),
            },
        };

        return Clone(settings) with { Providers = redactedProviders, CommLink = commLink, Host = host };
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
                    ApiKey = RestoreMask(p.ApiKey, currentProvider.ApiKey),
                    Endpoint = RestoreMaskRequired(p.Endpoint, currentProvider.Endpoint),
                }
                : p)
            .ToArray();

        CommLinkSettings mergedCommLink = request.CommLink with
        {
            WebhookUrl = RestoreMask(request.CommLink.WebhookUrl, current.CommLink.WebhookUrl),
        };

        HostSettings mergedHost = request.Host with
        {
            Https = request.Host.Https with
            {
                CertificatePassword = RestoreMask(
                    request.Host.Https.CertificatePassword,
                    current.Host.Https.CertificatePassword),
            },
        };

        return Clone(request) with { Providers = mergedProviders, CommLink = mergedCommLink, Host = mergedHost };
    }

    // W3.5: after MergeRedactedSecrets, any field still equal to the mask sentinel "***" is a residual the
    // merge could not restore — it can only come from a NEW provider (absent from current), where the
    // round-tripped redacted GET value would otherwise be persisted as the literal "***" (a silent
    // auth/config footgun). Reject those so the operator supplies real values.
    public static Result ValidateNoResidualMask(ArcanumSettings merged)
    {

        if (merged.Host.Https.CertificatePassword == MaskSentinel)
        {

            return Result.Failure(new Error(
                "Config.UnresolvedMask",
                $"Host.Https.CertificatePassword has a masked value ('{MaskSentinel}'); supply the real password."));

        }

        ProviderSettings[] providers = merged.Providers ?? [];

        foreach (ProviderSettings provider in providers)
        {

            if (provider.ApiKey == MaskSentinel)
            {

                return Result.Failure(new Error(
                    "Config.UnresolvedMask",
                    $"Provider '{provider.Name}' has a masked apiKey ('{MaskSentinel}'); supply the real key when adding a new provider."));

            }

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

    private static string? Mask(string? value) =>
        string.IsNullOrEmpty(value) ? value : "***";

    private static string MaskRequired(string value) =>
        string.IsNullOrEmpty(value) ? value : "***";

    private static string? RestoreMask(string? incoming, string? current) =>
        incoming == "***" ? current : incoming;

    private static string RestoreMaskRequired(string incoming, string current) =>
        incoming == "***" ? current : incoming;
}
