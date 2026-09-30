using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>What a write chokepoint may do with one identity, decided inside its transaction.</summary>
internal enum MemoryErasureGuardVerdict
{
    /// <summary>The store holds no evidence, or none that matches: the write proceeds.</summary>
    Allowed = 1,

    /// <summary>This exact identity was erased in this exact scope, so the write is refused.</summary>
    Withheld = 2,

    /// <summary>
    /// Evidence committed after the probe and before the transaction, and no key is in hand. The write
    /// rolls back, prepares again outside any transaction, and retries once.
    /// </summary>
    RetryWithKey = 3,

    /// <summary>The store holds evidence the key in hand cannot verify, so the write fails closed.</summary>
    KeyLost = 4,
}

/// <summary>What the phase before the transaction found, carried into it.</summary>
/// <remarks>
/// <see cref="Key"/> is a private copy the context owns and zeroes on disposal. It is set whenever the
/// store held evidence, and otherwise only when the latch already held a present key, which spares
/// most first-fingerprint races the retry without ever reading the credential store.
/// </remarks>
internal sealed class MemoryErasureGuardContext : IDisposable
{
    internal MemoryErasureGuardContext(MemoryReviewStore store, bool evidencePresent, MemoryErasureKey? key)
    {
        Store = store;

        EvidencePresent = evidencePresent;

        Key = key;
    }

    internal MemoryReviewStore Store { get; }

    /// <summary>Whether the probe before the transaction found any fingerprint for the store.</summary>
    internal bool EvidencePresent { get; }

    internal MemoryErasureKey? Key { get; }

    public void Dispose() => Key?.Dispose();
}

/// <summary>A guarded write refused for a reason its caller maps straight to a typed error.</summary>
internal sealed class MemoryErasureGuardException(Error error) : Exception(error.Message)
{
    internal Error Error { get; } = error;
}

/// <summary>
/// Thrown by a write that met <see cref="MemoryErasureGuardVerdict.RetryWithKey"/> after rolling back,
/// so <c>MemoryErasureGuard.RunWithRetryAsync</c> can resolve the key and run it again.
/// </summary>
internal sealed class MemoryErasureRetryException : Exception
{
    public MemoryErasureRetryException()
        : base("Erasure evidence appeared inside the write's transaction while no erasure key was in hand.")
    {
    }
}

/// <summary>
/// The two-phase key protocol every automatic write chokepoint follows before it records a memory an
/// operator may have erased.
/// </summary>
/// <remarks>
/// <para>Phase one, <see cref="PrepareAsync"/>, runs before any SQLite transaction, Covenant lease or
/// closure. It asks whether the store holds any fingerprint, and only when it does resolves the key
/// through the latch, so an installation that has erased nothing never touches the credential store.
/// A key that is not present, or that cannot verify the store's evidence, is refused here, before any
/// provider or model call.</para>
///
/// <para>Phase two, <c>CheckAsync</c>, runs inside the write's transaction and never trusts phase one:
/// evidence can commit in between. It asks again, then refuses evidence the key cannot verify, and
/// only then computes the identity and looks its fingerprint up. Credential I/O never happens inside
/// the transaction; a write that finds evidence with no key in hand goes back out for it instead.</para>
///
/// <para>Nothing here logs, and every refusal message is content-free.</para>
/// </remarks>
internal static class MemoryErasureGuard
{
    internal static Error KeyLostError { get; } = new(
        ErrorCodes.MemoryErasure.KeyLost,
        "Erasure fingerprints exist that this installation's erasure key cannot verify, so automatic writes to this store are withheld. Run 'arcanum memory erasure status'.");

    internal static Error KeyUnavailableError { get; } = new(
        ErrorCodes.MemoryErasure.KeyUnavailable,
        "The erasure key could not be read, so automatic writes to this store are withheld until it can be.");

    internal static Error UnavailableError { get; } = new(
        ErrorCodes.MemoryErasure.Unavailable,
        "Erasure evidence changed while the write was in progress; retry the write.");

    /// <summary>
    /// The one refusal for evidence met without a present key: an absent key is lost, and any state
    /// that might still resolve is unavailable.
    /// </summary>
    /// <remarks>A key whose identifier differs from the evidence's is lost too, whatever its state.</remarks>
    internal static Error RefusalFor(MemoryErasureKeyState state) =>
        state switch
        {
            MemoryErasureKeyState.Absent => KeyLostError,
            MemoryErasureKeyState.Unresolved
                or MemoryErasureKeyState.Unavailable
                or MemoryErasureKeyState.Malformed => KeyUnavailableError,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Only a key short of present is refused."),
        };

    /// <summary>
    /// Phase one: probes the store for evidence and, only when there is some, resolves the key.
    /// </summary>
    /// <returns>The context to carry into the transaction, or the refusal when the store cannot be guarded.</returns>
    internal static async Task<Result<MemoryErasureGuardContext>> PrepareAsync(
        SqliteConnection connection,
        MemoryReviewStore store,
        IMemoryErasureKeyProvider keys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(keys);

        if (!await MemoryErasureEvidence.AnyAsync(connection, null, store, cancellationToken).ConfigureAwait(false))
        {
            return new MemoryErasureGuardContext(store, evidencePresent: false, keys.TryCopyLatched());
        }

        // Probes the credential store at most once per process, and only while nothing is latched.
        MemoryErasureKeyOpenResult opened = keys.OpenExisting(MemoryErasureKeyProbe.UseLatched);

        if (opened.State is not MemoryErasureKeyState.Present)
        {
            opened.Key?.Dispose();

            return RefusalFor(opened.State);
        }

        MemoryErasureKey key = opened.Key
            ?? throw new InvalidOperationException("A present erasure key was opened without its material.");

        try
        {
            if (await MemoryErasureEvidence
                    .AnyForeignAsync(connection, null, store, key.KeyId.ToArray(), cancellationToken)
                    .ConfigureAwait(false))
            {
                key.Dispose();

                return KeyLostError;
            }
        }
        catch
        {
            key.Dispose();

            throw;
        }

        return new MemoryErasureGuardContext(store, evidencePresent: true, key);
    }

    /// <summary>Phase two, for an identity already in hand.</summary>
    internal static Task<MemoryErasureGuardVerdict> CheckAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MemoryErasureGuardContext context,
        MemoryErasureIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        RequireSameStore(context, identity);

        return CheckAsync(connection, transaction, context, () => identity, cancellationToken);
    }

    /// <summary>
    /// Phase two: re-probes inside the caller's transaction and decides, deriving the identity only
    /// when the store holds evidence this key can verify.
    /// </summary>
    /// <remarks>
    /// A write with no evidence never calls <paramref name="identity"/>, so deriving an identity from
    /// stored text - parsing a Campaign spelling, say - can neither slow nor fail it. When evidence
    /// exists, whatever the factory throws propagates, which fails the write closed.
    /// </remarks>
    /// <param name="transaction">
    /// The write's transaction, or null when the caller holds a raw <c>BEGIN IMMEDIATE</c> on the
    /// connection itself.
    /// </param>
    internal static async Task<MemoryErasureGuardVerdict> CheckAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MemoryErasureGuardContext context,
        Func<MemoryErasureIdentity> identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(context);

        ArgumentNullException.ThrowIfNull(identity);

        if (!await MemoryErasureEvidence.AnyAsync(connection, transaction, context.Store, cancellationToken).ConfigureAwait(false))
        {
            return MemoryErasureGuardVerdict.Allowed;
        }

        if (context.Key is not { } key)
        {
            return MemoryErasureGuardVerdict.RetryWithKey;
        }

        if (await MemoryErasureEvidence
                .AnyForeignAsync(connection, transaction, context.Store, key.KeyId.ToArray(), cancellationToken)
                .ConfigureAwait(false))
        {
            return MemoryErasureGuardVerdict.KeyLost;
        }

        MemoryErasureIdentity subject = identity();

        RequireSameStore(context, subject);

        return await MemoryErasureEvidence
                .ContainsAsync(connection, transaction, key.Fingerprint(subject), cancellationToken)
                .ConfigureAwait(false)
            ? MemoryErasureGuardVerdict.Withheld
            : MemoryErasureGuardVerdict.Allowed;
    }

    /// <summary>
    /// Runs one guarded write, preparing again and running it once more when it reports that evidence
    /// appeared with no key in hand.
    /// </summary>
    /// <remarks>
    /// <paramref name="write"/> owns its transaction, and throws <see cref="MemoryErasureRetryException"/>
    /// only after rolling it back, so the second preparation's credential read happens outside every
    /// transaction. A preparation refusal, or a second retry, surfaces as
    /// <see cref="MemoryErasureGuardException"/>. Each context is disposed once its attempt ends.
    /// </remarks>
    internal static async Task<T> RunWithRetryAsync<T>(
        SqliteConnection connection,
        MemoryReviewStore store,
        IMemoryErasureKeyProvider keys,
        Func<MemoryErasureGuardContext, Task<T>> write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        using (MemoryErasureGuardContext first = await PrepareOrThrowAsync(connection, store, keys, cancellationToken)
                   .ConfigureAwait(false))
        {
            try
            {
                return await write(first).ConfigureAwait(false);
            }
            catch (MemoryErasureRetryException)
            {
                // The write rolled back. The key is resolved below, outside its transaction.
            }
        }

        using MemoryErasureGuardContext second = await PrepareOrThrowAsync(connection, store, keys, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            return await write(second).ConfigureAwait(false);
        }
        catch (MemoryErasureRetryException)
        {
            throw new MemoryErasureGuardException(UnavailableError);
        }
    }

    private static async Task<MemoryErasureGuardContext> PrepareOrThrowAsync(
        SqliteConnection connection,
        MemoryReviewStore store,
        IMemoryErasureKeyProvider keys,
        CancellationToken cancellationToken)
    {
        Result<MemoryErasureGuardContext> prepared =
            await PrepareAsync(connection, store, keys, cancellationToken).ConfigureAwait(false);

        return prepared.IsSuccess ? prepared.Value : throw new MemoryErasureGuardException(prepared.Error);
    }

    private static void RequireSameStore(MemoryErasureGuardContext context, MemoryErasureIdentity identity)
    {
        if (identity.Store != context.Store)
        {
            throw new ArgumentException("An erasure guard answers only for the store it was prepared for.", nameof(identity));
        }
    }
}
