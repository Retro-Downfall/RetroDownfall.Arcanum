using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Cli.Services;

/// <summary>
/// Preserves the credential lease's typed diagnosis at every CLI transport boundary. A missing
/// credential, unreadable secure storage, a readable-but-mismatched credential, an unavailable
/// host, and an endpoint that failed presence verification are not interchangeable failures.
/// </summary>
internal static class ArcanumApiCredentialFailureMapper
{
    private const string MissingCredentialMessage =
        "No API key found. Run 'arcanum serve' once to generate and store a key.";

    private const string UnreadableCredentialMessage =
        "The local API credential is unreadable or corrupted. Run 'arcanum doctor' before retrying.";

    private const string CredentialMismatchMessage =
        "The server did not prove it owns this installation's credential. Run 'arcanum doctor' before retrying.";

    private const string UnverifiedLocalApiMessage =
        "The configured local endpoint did not provide a valid Arcanum presence proof. No reusable credential was sent.";

    private const string UnreachableMessage =
        "The Arcanum API is unreachable. Confirm 'arcanum serve' is running.";

    private const string TimeoutMessage =
        "The request to the Arcanum API timed out.";

    internal static Error ToError(ApiCredentialLeaseResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsVerified)
        {
            throw new ArgumentException(
                "A verified credential lease is not a credential failure.",
                nameof(result));
        }

        return result.CredentialStatus switch
        {
            SecretStoreReadStatus.Missing => new Error(
                ErrorCodes.Security.MissingApiKey,
                SanitizeGuidance(result.Guidance, MissingCredentialMessage)),
            SecretStoreReadStatus.Corrupted => new Error(
                ErrorCodes.Security.CredentialUnreadable,
                SanitizeGuidance(result.Guidance, UnreadableCredentialMessage)),
            SecretStoreReadStatus.Ok => new Error(
                ErrorCodes.Auth.Unauthorized,
                SanitizeGuidance(result.Guidance, CredentialMismatchMessage)),
            _ => MapTransient(result),
        };
    }

    internal static bool IsRetryable(ApiCredentialLeaseResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return !result.IsVerified
            && result.CredentialStatus is null
            && result.ProbeState is HealthProbeState.UnhealthyStatus
                or HealthProbeState.ConnectionRefused
                or HealthProbeState.NetworkUnreachable
                or HealthProbeState.DnsFailure
                or HealthProbeState.Timeout;
    }

    private static Error MapTransient(ApiCredentialLeaseResult result) =>
        result.ProbeState switch
        {
            HealthProbeState.Timeout => new Error(
                ErrorCodes.Connection.Timeout,
                SanitizeGuidance(result.Guidance, TimeoutMessage)),
            HealthProbeState.TlsFailure or HealthProbeState.UnexpectedResponder => new Error(
                ErrorCodes.Security.UnverifiedLocalApi,
                SanitizeGuidance(result.Guidance, UnverifiedLocalApiMessage)),
            HealthProbeState.Unauthorized => new Error(
                ErrorCodes.Auth.Unauthorized,
                SanitizeGuidance(result.Guidance, CredentialMismatchMessage)),
            _ => new Error(
                ErrorCodes.Connection.Unreachable,
                SanitizeGuidance(result.Guidance, UnreachableMessage)),
        };

    private static string SanitizeGuidance(string? guidance, string fallback)
    {
        if (string.IsNullOrWhiteSpace(guidance))
        {
            return fallback;
        }

        return string.Join(
            ' ',
            guidance.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
    }
}
