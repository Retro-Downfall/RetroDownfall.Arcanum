using System.Text.Json;
using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The durable checkpoint of a resumable accelerator rebuild.
/// </summary>
/// <remarks>
/// Every identity the rebuild captured travels with it, and the rebuilder rechecks all of them inside
/// the same transaction as each batch. A reset, an epoch change, or an outbox overflow between batches
/// therefore discards the partial generation instead of publishing it (§10.11).
///
/// <para><paramref name="Version"/> is an explicit discriminator rather than an implied one. A
/// payload whose version had to be inferred from which fields parsed would make an unknown future
/// shape look like a corrupt current one.</para>
/// </remarks>
public sealed record CovenantIndexRebuildCheckpointV1(
    int Version,
    Guid DatasetGeneration,
    ulong AcceleratorEpoch,
    long BaseTargetSearchSequence,
    long CapturedCoreCampaignDeletionSequence,
    CovenantIndexRebuildPhase Phase,
    long? BaseScanAfterSearchRowId,
    long LastContiguousAppliedSequence,
    long BaseHeadsProcessed,
    long? BaseHeadsTotal,
    long DeltaRowsProcessed)
{

    /// <summary>The only checkpoint version this build writes.</summary>
    public const int CurrentVersion = 1;

}

/// <summary>
/// The durable checkpoint of a Covenant family reinitialize.
/// </summary>
/// <remarks>
/// It stores no path, key, content, live handle, task, token, or service object — only identities,
/// digests, counters, and a closed phase. Reinitialize closes the database and drops a whole schema
/// family, so recovery cannot infer where it got to by inspecting the result: a half-dropped family
/// looks the same whether this process dropped it or found it that way (§10.16).
/// </remarks>
public sealed record CovenantFamilyReinitializeCheckpointV1(
    int Version,
    Guid OperationId,
    string InstallationIdentity,
    long AuthorityEpoch,
    string DatabaseFileIdentityDigest,
    string InspectedCatalogDigest,
    string EffectDigest,
    Guid OldDatasetGeneration,
    Guid? NewDatasetGeneration,
    CovenantFamilyReinitializePhase Phase,
    long ManagedArtifactCursor,
    bool OldFamilyDropped,
    bool CanonicalInstalled,
    bool AcceleratorInstalled,
    string? CompactedFileIdentityDigest,
    int RetryCount,
    string? LastDurableErrorCode)
{

    /// <summary>The only checkpoint version this build writes.</summary>
    public const int CurrentVersion = 1;

}

/// <summary>
/// The Infrastructure-owned serialization context for durable Covenant recovery payloads.
/// </summary>
/// <remarks>
/// Deliberately separate from <c>ArcanumJsonContext</c>, in the same way <c>BackupJsonContext</c> is.
/// A checkpoint is durable state read by a process that may be several releases newer than the one
/// that wrote it, and a public wire context is governed by an entirely different compatibility
/// promise. Sharing one context would mean a wire change could rewrite recovery state, and a recovery
/// change could break a client.
///
/// <para><see cref="JsonSerializerOptions.UnmappedMemberHandling"/> is <c>Disallow</c>, so a payload
/// carrying a field this build does not know fails recovery loudly instead of resuming with a
/// silently dropped invariant.</para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(CovenantIndexRebuildCheckpointV1))]
[JsonSerializable(typeof(CovenantFamilyReinitializeCheckpointV1))]
[JsonSerializable(typeof(CovenantOfflineTransitionLaunchV4))]
[JsonSerializable(typeof(DataRetentionFactoryTransitionLaunchV2))]
public sealed partial class CovenantRecoveryJsonContext : JsonSerializerContext
{

    /// <summary>
    /// The largest checkpoint payload recovery will decode.
    /// </summary>
    /// <remarks>
    /// Checked before decoding rather than after. Both shapes are fixed-width by construction, so
    /// anything larger is either corruption or an attempt to make recovery allocate.
    /// </remarks>
    public const int MaxCheckpointBytes = 4_096;

}

/// <summary>
/// The only way a durable Covenant recovery payload is written or read.
/// </summary>
/// <remarks>
/// Every failure is one code — <c>Covenant.ManualRecoveryRequired</c> — because they all have the
/// same remedy and none of them can be distinguished safely. A checkpoint that failed to decode
/// cannot be trusted to say why it failed, and an operator who is told "unknown field" versus
/// "wrong version" does the same thing in both cases: look at the operation, not at the bytes.
///
/// <para>Nothing here throws. Recovery runs before readiness, so an exception escaping the decoder
/// would take down a host over a payload the correct answer to is "leave this operation alone".</para>
/// </remarks>
public static partial class CovenantRecoveryCheckpointCodec
{

    public static byte[] Encode(CovenantIndexRebuildCheckpointV1 checkpoint) =>
        JsonSerializer.SerializeToUtf8Bytes(
            checkpoint,
            CovenantRecoveryJsonContext.Default.CovenantIndexRebuildCheckpointV1);

    public static byte[] Encode(CovenantFamilyReinitializeCheckpointV1 checkpoint) =>
        JsonSerializer.SerializeToUtf8Bytes(
            checkpoint,
            CovenantRecoveryJsonContext.Default.CovenantFamilyReinitializeCheckpointV1);

    public static Result<CovenantIndexRebuildCheckpointV1> DecodeIndexRebuild(ReadOnlySpan<byte> payload) =>
        Decode(
            payload,
            static bytes => JsonSerializer.Deserialize(
                bytes,
                CovenantRecoveryJsonContext.Default.CovenantIndexRebuildCheckpointV1),
            static checkpoint => checkpoint.Version == CovenantIndexRebuildCheckpointV1.CurrentVersion);

    public static Result<CovenantFamilyReinitializeCheckpointV1> DecodeFamilyReinitialize(ReadOnlySpan<byte> payload) =>
        Decode(
            payload,
            static bytes => JsonSerializer.Deserialize(
                bytes,
                CovenantRecoveryJsonContext.Default.CovenantFamilyReinitializeCheckpointV1),
            static checkpoint => checkpoint.Version == CovenantFamilyReinitializeCheckpointV1.CurrentVersion);

    /// <summary>
    /// The canonical encoding of a 32-byte effect digest: exactly 64 lowercase hexadecimal
    /// characters.
    /// </summary>
    /// <remarks>
    /// Lowercase rather than case-insensitive, so one effect has one encoding. Two spellings of the
    /// same digest would compare unequal as durable text while denoting the same destructive plan,
    /// and the adoption check is the one place that must never be nearly right.
    /// </remarks>
    public static string EncodeEffectDigest(CovenantDigest digest) =>
        Convert.ToHexStringLower(digest.Bytes);

    private static bool IsCanonicalEffectDigest(string? digest) =>
        digest is { Length: 64 }
        && digest.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static Result<T> Decode<T>(
        ReadOnlySpan<byte> payload,
        Func<byte[], T?> deserialize,
        Func<T, bool> versionMatches)
        where T : class
    {

        // The bound is checked against the raw length before anything parses, so a hostile or
        // corrupt payload cannot make recovery allocate in proportion to itself.
        if (payload.Length is 0 or > CovenantRecoveryJsonContext.MaxCheckpointBytes)
        {

            return Unrecoverable<T>();

        }

        try
        {

            return deserialize(payload.ToArray()) is { } decoded && versionMatches(decoded)
                ? Result<T>.Success(decoded)
                : Unrecoverable<T>();

        }
        catch (JsonException)
        {

            return Unrecoverable<T>();

        }
        catch (NotSupportedException)
        {

            return Unrecoverable<T>();

        }

    }

    private static Result<T> Unrecoverable<T>() =>
        Result<T>.Failure(
            new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "This Covenant recovery checkpoint cannot be resumed by this build."));

}
