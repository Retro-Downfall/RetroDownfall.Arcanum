using System.Net.Http;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Compendium.Ux.Services;

/// <summary>Whether a Familiar's CLI is installed and signed in, as reported by the running host.</summary>
public interface IFamiliarProbeClient
{

    Task<Result<FamiliarProbeResult>> ProbeAsync(string providerName, CancellationToken cancellationToken);

}

/// <summary>
/// The one thing Compendium asks the host for.
/// </summary>
/// <remarks>
/// <para>
/// Compendium is an editor: it reads and writes <c>arcanum.json</c> directly, transactionally, and
/// does not run inference, open the Grimoire, execute tools, or manage the daemon. Probing a
/// Familiar means spawning a process, and Infrastructure already owns that discipline — so rather
/// than growing a second process-spawn implementation here, Compendium asks the host over HTTP and
/// keeps configuration persistence exactly as it was.
/// </para>
/// <para>
/// Read-only by construction: there is no write path on this client, and the endpoint it calls is
/// non-billable. The host not running is a first-class answer, not a failure to hide — the caller
/// renders the remediation and leaves the hide list fully editable by hand.
/// </para>
/// </remarks>
public sealed class FamiliarProbeClient(
    IArcanumConfigurationStore store,
    ISecretStore secretStore,
    IHttpClientFactory? httpClientFactory = null) : IFamiliarProbeClient
{

    /// <summary>
    /// Short on purpose. This backs a button in a settings editor, so a host that is starting up, or
    /// wedged, has to resolve into "probe unavailable" quickly rather than into a spinner.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    internal const string HostUnavailableCode = "Compendium.HostUnavailable";

    internal const string HostUnavailableMessage =
        "Probe unavailable — the Arcanum host is not running. Start it with `arcanum serve` and re-probe. "
        + "The hidden-models list stays editable either way.";

    internal const string CertificateUntrustedCode = "Compendium.HostCertificateUntrusted";

    /// <summary>
    /// A rejected chain is not a stopped host, and saying so sends the operator to restart something
    /// that is already running. Under ListenAny the probe target is HTTPS, and the certificate
    /// Compendium generates is loopback-SAN only and is never installed into an OS trust store — so
    /// this is the expected first failure there, and it needs its own remediation.
    /// </summary>
    internal static string CertificateUntrustedMessage(string endpoint, string detail) =>
        $"Probe unavailable — {endpoint} presented a TLS certificate this machine does not trust, so the probe "
        + "never reached the host. The host may well be running: trust that certificate (a Compendium-generated "
        + "one is loopback-SAN only and is not installed into your OS trust store) or configure one that is "
        + $"already trusted, then re-probe. The hidden-models list stays editable either way. ({detail})";

    public async Task<Result<FamiliarProbeResult>> ProbeAsync(
        string providerName,
        CancellationToken cancellationToken)
    {

        ArcanumSettings settings;

        string? apiKey;

        try
        {

            settings = await store.ReadAsync(cancellationToken).ConfigureAwait(false);

            SecretStoreReadResult apiKeyRead = await secretStore
                .PeekApiKeyReadResultAsync()
                .ConfigureAwait(false);

            apiKey = apiKeyRead.Status == SecretStoreReadStatus.Ok
                ? apiKeyRead.Value
                : null;

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {

            return Result<FamiliarProbeResult>.Failure(
                new Error(HostUnavailableCode, HostUnavailableMessage));

        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {

            return Result<FamiliarProbeResult>.Failure(
                new Error(
                    HostUnavailableCode,
                    "Probe unavailable — no API key is stored for this installation yet. Run `arcanum serve` once to create one."));

        }

        using HttpClient client = CreateClient(settings);

        client.DefaultRequestHeaders.Add("X-Arcanum-Key", apiKey);

        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        deadline.CancelAfter(RequestTimeout);

        try
        {

            using HttpResponseMessage response = await client
                .GetAsync(
                    $"api/providers/{Uri.EscapeDataString(providerName)}/familiar-probe",
                    deadline.Token)
                .ConfigureAwait(false);

            string json = await response.Content
                .ReadAsStringAsync(deadline.Token)
                .ConfigureAwait(false);

            ApiResponse<FamiliarProbeResult>? envelope = JsonSerializer.Deserialize(
                json,
                ConfigurationJsonContext.Default.ApiResponseFamiliarProbeResult);

            if (envelope?.Data is { } probed && envelope.IsSuccess)
            {
                return Result<FamiliarProbeResult>.Success(probed);
            }

            return Result<FamiliarProbeResult>.Failure(
                envelope?.Error
                ?? new Error(HostUnavailableCode, HostUnavailableMessage));

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException)
        {

            // Host down, still starting, or answering something Compendium cannot read. Those are
            // the same actionable state for the operator, and none of them is an error dialog — but
            // a certificate the machine will not trust is a different remedy and gets its own.
            return Result<FamiliarProbeResult>.Failure(
                DescribeTransportFailure(ex, client.BaseAddress));

        }

    }

    private static Error DescribeTransportFailure(Exception exception, Uri? endpoint)
    {

        if (!IsCertificateRejection(exception))
        {

            return new Error(HostUnavailableCode, HostUnavailableMessage);

        }

        return new Error(
            CertificateUntrustedCode,
            CertificateUntrustedMessage(
                endpoint?.ToString() ?? "the configured HTTPS endpoint",
                exception.Message));

    }

    private static bool IsCertificateRejection(Exception exception)
    {

        if (exception is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError })
        {

            return true;

        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {

            if (current is AuthenticationException)
            {

                return true;

            }

        }

        return false;

    }

    private HttpClient CreateClient(ArcanumSettings settings)
    {

        HttpClient client = httpClientFactory?.CreateClient(nameof(FamiliarProbeClient)) ?? new HttpClient();

        client.BaseAddress = new Uri(ArcanumLocalApiAddress.ResolveBaseUrl(settings.Host));

        client.Timeout = RequestTimeout;

        return client;

    }

}
