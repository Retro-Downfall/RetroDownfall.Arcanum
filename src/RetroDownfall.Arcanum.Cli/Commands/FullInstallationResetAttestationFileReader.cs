using System.Text.Json;

using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Cli.Commands;

internal interface IFullInstallationResetAttestationFileReader
{

    Task<Result<FullInstallationResetExternalRemediationAttestation>> ReadAsync(
        string path,
        CancellationToken cancellationToken);

}

internal sealed class FullInstallationResetAttestationFileReader
    : IFullInstallationResetAttestationFileReader
{

    internal const int MaximumBytes = 64 * 1024;

    private static readonly CliJsonContext AttestationJsonContext = new(
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

            MaxDepth = 16,

            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        });

    internal static int DecoderMaximumDepth =>
        AttestationJsonContext.Options.MaxDepth;

    internal static JsonUnmappedMemberHandling DecoderUnmappedMemberHandling =>
        AttestationJsonContext.Options.UnmappedMemberHandling;

    public async Task<Result<FullInstallationResetExternalRemediationAttestation>> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(path))
        {

            return Invalid();

        }

        using SecureFileReadResult read = await SecureFileReader.ReadBytesAsync(
                path,
                MaximumBytes,
                cancellationToken,
                requireOwnerControlled: true)
            .ConfigureAwait(false);

        if (read.Status is not SecureFileReadStatus.Success)
        {

            return Invalid();

        }

        try
        {

            FullInstallationResetExternalRemediationAttestation? attestation =
                JsonSerializer.Deserialize(
                    read.Bytes.Span,
                    AttestationJsonContext
                        .FullInstallationResetExternalRemediationAttestation);

            return attestation is null
                ? Invalid()
                : Result<FullInstallationResetExternalRemediationAttestation>
                    .Success(attestation);

        }
        catch (Exception exception) when (
            exception is JsonException
                or NotSupportedException
                or ArgumentException)
        {

            return Invalid();

        }

    }

    private static Result<FullInstallationResetExternalRemediationAttestation> Invalid() =>
        Result<FullInstallationResetExternalRemediationAttestation>.Failure(
            new Error(
                ErrorCodes.Data.ExternalRemediationInvalid,
                "The external remediation attestation could not be read securely."));

}
