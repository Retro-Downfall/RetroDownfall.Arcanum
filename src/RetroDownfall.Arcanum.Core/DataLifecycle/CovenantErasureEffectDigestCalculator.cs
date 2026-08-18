using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.DataLifecycle;

/// <summary>
/// Everything about a Covenant erasure that decides what it is allowed to destroy.
/// </summary>
/// <remarks>
/// The authenticated canonical plan, reduced to its identity and its content-free inventory. It
/// carries no subject text, key, digest preimage, path, or Campaign identity, because the digest it
/// produces travels in a durable checkpoint that operator surfaces read back.
///
/// <para>The plan id rather than the plan object: a plan id already commits to every candidate,
/// blocker, and conflict the planner resolved, and hashing the projection again here would be a
/// second opinion about what the operator was shown.</para>
/// </remarks>
public sealed record CovenantErasureEffectDigestInput(
    CovenantExclusiveOperation Operation,
    string PlanId,
    Guid DatasetGeneration,
    long Rows,
    long ManagedFiles,
    long LocalArtifacts,
    long AffectedSessions,
    long PossibleDisclosures,
    CovenantDisclosureCountKind DisclosureCountKind);

/// <summary>
/// The sole producer of a Covenant erasure's effect digest.
/// </summary>
/// <remarks>
/// One producer, because the digest is the identity every later stage compares against: the
/// exclusive gate acquisition, the durable <c>InventoryPrepared</c> checkpoint, the normalized
/// request-identity row, and recovery's adoption check. Two implementations that agreed on the day
/// they were written would, at the first divergence, let a retry with a changed plan resume a
/// half-finished erasure (§10.20.3).
/// </remarks>
public interface ICovenantErasureEffectDigestCalculator
{

    Result<CovenantDigest> Compute(CovenantErasureEffectDigestInput input);

}

/// <summary>
/// The one implementation.
/// </summary>
public sealed class CovenantErasureEffectDigestCalculator : ICovenantErasureEffectDigestCalculator
{

    /// <summary>The pinned domain of an ordinary Covenant reset.</summary>
    public const string ResetDomain = "Arcanum.Covenant.Reset.Effect.v1";

    /// <summary>The pinned domain of a healthy-catalog factory erasure.</summary>
    public const string HealthyCatalogFactoryErasureDomain =
        "Arcanum.Covenant.HealthyCatalogFactoryErasure.Effect.v1";

    /// <summary>
    /// The ASCII wire value each disclosure-count kind contributes, length-prefixed.
    /// </summary>
    /// <remarks>
    /// The wire value rather than the numeric code, so a future reordering of the enum cannot turn
    /// one plan's exact count into another's lower bound while producing the same digest.
    /// </remarks>
    private static readonly Dictionary<CovenantDisclosureCountKind, string> CountKindWireValues = new()
    {
        [CovenantDisclosureCountKind.Exact] = "Exact",

        [CovenantDisclosureCountKind.LowerBound] = "LowerBound",
    };

    public Result<CovenantDigest> Compute(CovenantErasureEffectDigestInput input)
    {

        if (input is null)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A Covenant erasure effect digest requires a complete plan.");

        }

        // Only the two erasure operations have one. Every other exclusive operation already owns its
        // own effect identity, and minting one here would give two producers to a single owner.
        string? domain = input.Operation switch
        {

            CovenantExclusiveOperation.CovenantReset => ResetDomain,

            CovenantExclusiveOperation.HealthyCatalogFactoryErasure =>
                HealthyCatalogFactoryErasureDomain,

            _ => null,

        };

        if (domain is null)
        {

            return new Error(
                ErrorCodes.Covenant.InvalidScope,
                "Only a Covenant reset or a healthy-catalog factory erasure has an erasure effect digest.");

        }

        if (!CountKindWireValues.TryGetValue(input.DisclosureCountKind, out string? countKind))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The disclosure count kind is not one this build can authorize.");

        }

        if (string.IsNullOrWhiteSpace(input.PlanId) || input.DatasetGeneration == Guid.Empty)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A Covenant erasure effect digest requires an identified plan and dataset.");

        }

        long[] counts =
        [
            input.Rows,
            input.ManagedFiles,
            input.LocalArtifacts,
            input.AffectedSessions,
            input.PossibleDisclosures,
        ];

        // A negative inventory is a measurement bug rather than a plan. Hashing it would mint a
        // stable owner for a destructive operation nobody could have produced.
        if (counts.Any(static count => count < 0))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A Covenant erasure inventory cannot carry a negative count.");

        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(Encoding.ASCII.GetBytes(domain));

        hash.AppendData([0x00]);

        AppendLengthPrefixed(hash, Encoding.UTF8.GetBytes(input.PlanId));

        Span<byte> uuid = stackalloc byte[16];

        _ = input.DatasetGeneration.TryWriteBytes(uuid, bigEndian: true, out _);

        hash.AppendData(uuid);

        Span<byte> value = stackalloc byte[sizeof(ulong)];

        foreach (long count in counts)
        {

            BinaryPrimitives.WriteUInt64BigEndian(value, (ulong)count);

            hash.AppendData(value);

        }

        AppendLengthPrefixed(hash, Encoding.ASCII.GetBytes(countKind));

        return new CovenantDigest(hash.GetHashAndReset());

    }

    private static void AppendLengthPrefixed(IncrementalHash hash, byte[] utf8)
    {

        Span<byte> length = stackalloc byte[sizeof(uint)];

        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)utf8.Length));

        hash.AppendData(length);

        hash.AppendData(utf8);

    }

}
