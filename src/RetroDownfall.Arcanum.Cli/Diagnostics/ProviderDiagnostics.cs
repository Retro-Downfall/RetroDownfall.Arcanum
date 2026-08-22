using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.Services.Setup;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Cli.Diagnostics;

/// <summary>
/// Reachability of every configured inference provider, using the same non-billable
/// <c>GET {endpoint}/models</c> probe the setup wizard uses (ADR 0002 non-billable surfaces). No
/// completion is ever requested, so a diagnostic can never spend inference tokens.
///
/// <para>This is the only check in the catalog that sends a packet, so it is opt-in behind
/// <c>--include-network</c>: <c>arcanum doctor</c> is a local-first verb, and an operator debugging a
/// broken installation should not have to reason about outbound egress they did not ask for.</para>
///
/// <para>Endpoints are never printed. <c>SetupProviderProbe</c> treats the endpoint as a sensitive
/// configuration value and reports only a network class, and this check keeps that rule — the
/// provider name is enough to act on, and the URL may embed a tenant or host identifier.</para>
/// </summary>
public sealed class ProviderReachabilityCheck(
    IOptions<ArcanumSettings> options,
    IProviderApiKeyResolver apiKeyResolver,
    ISetupProviderProbe probe) : IDoctorCheck
{

    public const string CheckId = "providers.reachability";

    public string Id => CheckId;

    public string Name => "Provider reachability";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Providers;

    public bool RequiresNetwork => true;

    public async Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
    {

        IReadOnlyList<ProviderSettings> providers =
            [.. (options.Value.Providers ?? []).Where(static provider => !string.IsNullOrWhiteSpace(provider.Name))];

        if (providers.Count == 0)
        {

            return new DoctorFinding(
                DoctorOutcome.Skipped,
                "No inference provider is configured.");

        }

        List<string> summaries = [];

        List<DoctorRemedy> remedies = [];

        DoctorOutcome worst = DoctorOutcome.Healthy;

        foreach (ProviderSettings provider in providers)
        {

            cancellationToken.ThrowIfCancellationRequested();

            string? apiKey = await apiKeyResolver
                .PeekAsync(provider, cancellationToken)
                .ConfigureAwait(false);

            SetupConnectivityResult result = await probe
                .ProbeAsync(provider.Endpoint, null, apiKey, cancellationToken)
                .ConfigureAwait(false);

            (DoctorOutcome outcome, string summary, DoctorRemedy? remedy) = Classify(provider.Name, result);

            summaries.Add(summary);

            if (remedy is not null)
            {

                remedies.Add(remedy);

            }

            if (outcome > worst)
            {

                worst = outcome;

            }

        }

        return new DoctorFinding(worst, string.Join("; ", summaries), remedies);

    }

    internal static (DoctorOutcome Outcome, string Summary, DoctorRemedy? Remedy) Classify(
        string providerName,
        SetupConnectivityResult result) => result.Status switch
        {
            SetupConnectivityStatus.Reachable => (
                DoctorOutcome.Healthy,
                $"{providerName}: reachable, {result.ModelsAdvertised} model(s) advertised ({result.LatencyMs} ms)",
                null),

            SetupConnectivityStatus.AuthenticationFailed => (
                DoctorOutcome.Unhealthy,
                $"{providerName}: the endpoint rejected the credential",
                new DoctorRemedy(
                    "providers.replace_credential",
                    null,
                    DoctorRemedyCommands.KeyProviderSetFor(providerName),
                    "Store a current credential for this provider. The stored value is never displayed.")),

            SetupConnectivityStatus.EndpointRejected => (
                DoctorOutcome.Unhealthy,
                $"{providerName}: the configured endpoint was rejected before any request left this process",
                new DoctorRemedy(
                    "providers.correct_endpoint",
                    null,
                    DoctorRemedyCommands.ConfigEdit,
                    "Correct this provider's endpoint. It must be an absolute, resolvable, egress-allowed URL.")),

            SetupConnectivityStatus.TlsFailure => (
                DoctorOutcome.Unhealthy,
                $"{providerName}: the TLS handshake failed",
                new DoctorRemedy(
                    "providers.trust_certificate",
                    null,
                    DoctorRemedyCommands.ConfigEdit,
                    "Trust the endpoint's certificate in the OS store, or correct the endpoint. Arcanum never bypasses TLS validation.")),

            SetupConnectivityStatus.ModelMissing => (
                DoctorOutcome.Degraded,
                $"{providerName}: reachable, but it does not advertise the configured model",
                new DoctorRemedy(
                    "providers.correct_model",
                    null,
                    DoctorRemedyCommands.ModelList,
                    "Pick a model this provider actually advertises.")),

            SetupConnectivityStatus.Timeout => (
                DoctorOutcome.Unavailable,
                $"{providerName}: the model listing timed out",
                null),

            SetupConnectivityStatus.MalformedResponse => (
                DoctorOutcome.Degraded,
                $"{providerName}: reachable, but the model listing was not an OpenAI-compatible document",
                null),

            SetupConnectivityStatus.NotAttempted => (
                DoctorOutcome.Skipped,
                $"{providerName}: not probed",
                null),

            _ => (
                DoctorOutcome.Unavailable,
                $"{providerName}: unreachable",
                null),
        };

}

/// <summary>
/// Whether the web-research (Perplexity) credential is present and decryptable.
///
/// <para>Presence only, deliberately. Perplexity exposes no non-billable endpoint — its single
/// documented surface is a billed completion — so a reachability probe here would spend the
/// operator's quota every time they ran a diagnostic, which issue #33's own constraint forbids.
/// Credential status is the strongest signal that can be collected for free, and an expired key
/// still surfaces as a corrupt or missing credential rather than silently.</para>
/// </summary>
public sealed class WebResearchCredentialCheck(
    IOptions<ArcanumSettings> options,
    IWebResearchCredentialStore credentialStore) : IDoctorCheck
{

    public const string CheckId = "webresearch.credential";

    public string Id => CheckId;

    public string Name => "Web research credential";

    public DoctorSubsystem Subsystem => DoctorSubsystem.WebResearch;

    public bool RequiresNetwork => false;

    public async Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
    {

        WebBrowsingSettings webResearch = options.Value.ResolveWebBrowsing();

        if (!webResearch.Enabled)
        {

            return new DoctorFinding(
                DoctorOutcome.Skipped,
                "Web research is disabled, so no credential is required.");

        }

        if (EnvironmentCredentialResolver.ResolveWebResearchApiKey(webResearch) is not null)
        {

            return new DoctorFinding(
                DoctorOutcome.Healthy,
                "The web-research credential resolves from its environment reference. The value is never shown.");

        }

        SecretStoreReadResult stored = await credentialStore
            .PeekPerplexityApiKeyReadResultAsync(cancellationToken)
            .ConfigureAwait(false);

        return stored.Status switch
        {
            SecretStoreReadStatus.Ok => new DoctorFinding(
                DoctorOutcome.Healthy,
                "The web-research credential is present in the secure store. The value is never shown. "
                + "Reachability is not probed because this provider bills every request."),

            SecretStoreReadStatus.Corrupted => new DoctorFinding(
                DoctorOutcome.Unhealthy,
                "The stored web-research credential cannot be decrypted, so every research call will fail.",
                [ReplaceCredential]),

            _ => new DoctorFinding(
                DoctorOutcome.Degraded,
                "Web research is enabled but no credential is stored, so every research call will fail.",
                [ReplaceCredential]),
        };

    }

    private static DoctorRemedy ReplaceCredential =>
        new(
            "webresearch.replace_credential",
            null,
            DoctorRemedyCommands.KeyProviderSetPerplexity,
            "Store a current web-research credential from redirected stdin or a secure prompt. The value is never displayed.");

}
