using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The erasure key a Covenant write carries into its transaction, and the one mapping from Covenant
/// erasure evidence to what an agent may author.
/// </summary>
/// <remarks>
/// <para>A gate is read from the process's latch before any transaction begins and is never anything
/// but a latch read: <see cref="FromLatch"/> copies a key the latch already holds, and otherwise records
/// the state the latch is in. Nothing here reads the OS credential store, so a gate can be captured
/// while a Covenant lease is held and classified inside a transaction without keychain I/O in either
/// place. A key a gate holds is a private copy, zeroed when the gate is disposed.</para>
///
/// <para>The write authority and the staging probes classify through <see cref="ClassifyAsync"/> and
/// refuse through <see cref="RefusalFor"/>, so the courtesy refusal at staging and the enforcement
/// inside publication cannot disagree. The classification is deliberately lane- and epoch-free: a
/// fingerprint names a scoped identity, not a head.</para>
///
/// <para>Nothing here logs, and every refusal is content-free: a fingerprinted identity is refused with
/// the pin's own code and text, and key trouble with the errors every other chokepoint answers.</para>
/// </remarks>
internal sealed record CovenantAgentErasureGate(MemoryErasureKey? Key, MemoryErasureKeyState LatchState) : IDisposable
{
    /// <summary>The refusal a pinned lane and an erased key share, so an agent cannot tell them apart.</summary>
    internal const string OperatorManagedRefusal = "This Covenant key is managed by the operator in this scope.";

    /// <summary>
    /// A gate that holds no key and has read no latch: enough for any write that meets no Covenant
    /// evidence, and refused as unavailable by any that does. It owns nothing to dispose.
    /// </summary>
    internal static readonly CovenantAgentErasureGate None = new(null, MemoryErasureKeyState.Unresolved);

    /// <summary>Reads the gate from the latch alone. Never performs credential I/O.</summary>
    /// <remarks>
    /// The key is copied first. When no copy was taken, the state is read afterwards, and a Present read
    /// there raced a publish between the two reads: with no key in hand it is as unresolved as a latch
    /// nobody has asked.
    /// </remarks>
    internal static CovenantAgentErasureGate FromLatch(IMemoryErasureKeyProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (provider.TryCopyLatched() is { } key)
        {
            return new CovenantAgentErasureGate(key, MemoryErasureKeyState.Present);
        }

        MemoryErasureKeyState state = provider.Latch.State;

        return new CovenantAgentErasureGate(
            null,
            state is MemoryErasureKeyState.Present ? MemoryErasureKeyState.Unresolved : state);
    }

    /// <summary>The one refusal for each classification; <see langword="null"/> for a clear one.</summary>
    internal static Error? RefusalFor(CovenantAgentErasureState state) =>
        state switch
        {
            CovenantAgentErasureState.Clear => null,
            CovenantAgentErasureState.Withheld => new Error(ErrorCodes.Covenant.ForbiddenAuthority, OperatorManagedRefusal),
            CovenantAgentErasureState.KeyUnavailable => MemoryErasureGuard.KeyUnavailableError,
            CovenantAgentErasureState.KeyLost => MemoryErasureGuard.KeyLostError,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "A recognized Covenant erasure state is required."),
        };

    /// <summary>
    /// Classifies an agent authoring one exact scoped key, on the caller's connection and inside the
    /// caller's transaction.
    /// </summary>
    /// <remarks>
    /// A store with no Covenant evidence, or a catalog that cannot hold any, is clear without a key.
    /// Evidence with no key in hand is key loss when the latch proved the key absent, and unavailable
    /// otherwise. Evidence this key did not record can never be matched, so it is key loss too. Only
    /// then is the identity fingerprinted and looked up.
    /// </remarks>
    internal async Task<CovenantAgentErasureState> ClassifyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantScope scope,
        Guid? campaignId,
        string normalizedKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentNullException.ThrowIfNull(normalizedKey);

        // A span cannot cross an await, so the identifier is copied out before the first one.
        byte[]? keyId = Key?.KeyId.ToArray();

        if (!await MemoryErasureEvidence
                .AnyAsync(connection, transaction, MemoryReviewStore.Covenant, cancellationToken)
                .ConfigureAwait(false))
        {
            return CovenantAgentErasureState.Clear;
        }

        if (Key is not { } key || keyId is null)
        {
            return LatchState is MemoryErasureKeyState.Absent
                ? CovenantAgentErasureState.KeyLost
                : CovenantAgentErasureState.KeyUnavailable;
        }

        if (await MemoryErasureEvidence
                .AnyForeignAsync(connection, transaction, MemoryReviewStore.Covenant, keyId, cancellationToken)
                .ConfigureAwait(false))
        {
            return CovenantAgentErasureState.KeyLost;
        }

        byte[] fingerprint = key.Fingerprint(MemoryErasureIdentity.ForCovenant(scope, campaignId, normalizedKey));

        return await MemoryErasureEvidence
                .ContainsAsync(connection, transaction, fingerprint, cancellationToken)
                .ConfigureAwait(false)
            ? CovenantAgentErasureState.Withheld
            : CovenantAgentErasureState.Clear;
    }

    /// <summary>Zeroes the key this gate copied. Idempotent; <see cref="None"/> owns no key.</summary>
    public void Dispose() => Key?.Dispose();
}
