using System.Data;
using System.Data.Common;
using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Saga extraction's side of the erasure chokepoint: the page gate before the extraction model is
/// called, and the check that keeps an erased conclusion from being sent to the embedding provider
/// again.
/// </summary>
/// <remarks>
/// <para><b>Advisory, not authoritative.</b> <c>SagaMemoryStore.InsertCoreAsync</c> is the chokepoint
/// that decides, inside the insert's own transaction. What this adds is timing: extraction learns
/// before it pays for a model call that it cannot know what it may write, and learns before an
/// embedding call that a candidate would be refused anyway, so the erased text never leaves the
/// installation again.</para>
///
/// <para>Both answers use the same two-phase guard and the same identity the insert does, over the
/// scoped connection the store shares, so extraction and the store cannot disagree about what was
/// erased.</para>
/// </remarks>
internal sealed class SagaErasureWriteGate(ArcanumDbContext db, IMemoryErasureKeyProvider erasureKeys)
{
    /// <summary>
    /// Phase one for one extraction page: whether Saga evidence exists, and if so the key to check it.
    /// </summary>
    /// <remarks>
    /// Runs with no transaction open, so the one credential read it may make is never taken under a
    /// SQLite lock. A failure means Saga fingerprints exist that this installation cannot check, and
    /// the page is deferred before any provider sees it.
    /// </remarks>
    internal async Task<Result<MemoryErasureGuardContext>> PrepareAsync(CancellationToken cancellationToken)
    {
        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await MemoryErasureGuard
            .PrepareAsync((SqliteConnection)connection, MemoryReviewStore.Saga, erasureKeys, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the insert would refuse <paramref name="content"/> from <paramref name="sessionId"/>
    /// because an operator erased that exact content in that exact scope.
    /// </summary>
    /// <remarks>
    /// The scope is derived exactly as the insert derives it, from the Session's canonical binding.
    /// Evidence this context cannot verify, or that appeared after the page was prepared with no key in
    /// hand, is not an answer: it throws, so the page is retried and its cursor holds.
    /// </remarks>
    /// <exception cref="MemoryErasureGuardException">The evidence cannot be checked with the key in hand.</exception>
    /// <exception cref="FormatException">The Session's bound Campaign is not an identity a fingerprint can name.</exception>
    internal async Task<bool> IsWithheldAsync(
        MemoryErasureGuardContext context,
        Guid? sessionId,
        string content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        (SagaMemoryScopeKind kind, string? campaignId) = await SagaMemoryScopeClassifier
            .ResolveForSessionAsync(connection, null, sessionId, cancellationToken)
            .ConfigureAwait(false);

        MemoryErasureGuardVerdict verdict = await MemoryErasureGuard
            .CheckAsync(
                (SqliteConnection)connection,
                null,
                context,
                () => SagaIdentity(kind, campaignId, content),
                cancellationToken)
            .ConfigureAwait(false);

        return verdict switch
        {
            MemoryErasureGuardVerdict.Allowed => false,
            MemoryErasureGuardVerdict.Withheld => true,
            MemoryErasureGuardVerdict.KeyLost => throw new MemoryErasureGuardException(MemoryErasureGuard.KeyLostError),
            _ => throw new MemoryErasureGuardException(MemoryErasureGuard.UnavailableError),
        };
    }

    /// <summary>
    /// The erasure identity of Saga content in the scope the classifier derived for it.
    /// </summary>
    /// <remarks>
    /// <para>The Campaign is parsed to its GUID bytes, so every spelling of one Campaign names one
    /// identity. A value that is not a GUID never falls back to its raw text: a loose match could
    /// withhold content erased nowhere, or miss content erased here.</para>
    ///
    /// <para>A stored pairing no fingerprint can describe - a Campaign named outside Campaign scope, or
    /// Campaign scope with none - is refused as the same <see cref="FormatException"/>, so every caller
    /// handles one exception type and fails closed.</para>
    /// </remarks>
    /// <exception cref="FormatException">The Campaign is not a GUID, or does not match the scope.</exception>
    internal static MemoryErasureIdentity SagaIdentity(SagaMemoryScopeKind kind, string? campaignId, string content)
    {
        Guid? campaign = campaignId is null ? null : Guid.Parse(campaignId, CultureInfo.InvariantCulture);

        try
        {
            return MemoryErasureIdentity.ForSaga(kind, campaign, content);
        }
        catch (ArgumentException inconsistent)
        {
            throw new FormatException(
                "A Saga memory's stored scope and Campaign do not form an identity an erasure fingerprint can name.",
                inconsistent);
        }
    }

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }
}
