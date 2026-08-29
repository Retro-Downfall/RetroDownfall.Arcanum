using System.Data.Common;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// The one answer to "whose memory is this", shared by the writer that records a new memory's scope and
/// the sweep that classifies the ones written before scopes existed.
/// </summary>
/// <remarks>
/// Shared rather than mirrored. Two copies of this rule would be two ideas of what a missing binding
/// means, and the disagreement would show up as a memory that is retrievable in one Campaign after an
/// upgrade and in none after a fresh write - a difference nobody would look for.
///
/// <para>Authority comes from <c>session_campaign_bindings</c> and never from
/// <c>Sessions.CampaignId</c>. The binding is the immutable statement of what a Session is bound to; the
/// navigation column is the legacy field the binding was introduced to stop anyone reading as
/// authority.</para>
///
/// <para>The Campaign identity it hands on is canonicalized rather than copied verbatim - see
/// <see cref="Classify"/> - so a memory's scope is decided by <i>which</i> Campaign the binding names
/// and never by how that Campaign happens to be spelled on the row it was read from.</para>
/// </remarks>
internal static class SagaMemoryScopeClassifier
{

    /// <summary>
    /// Decides a memory's scope from facts already read.
    /// </summary>
    /// <remarks>
    /// A memory with no owning Session was never bound to a Campaign, which is the one case that is
    /// genuinely installation-scoped. Every answer the binding does not give - unresolved, or no binding
    /// at all because the Session is gone - is <see cref="SagaMemoryScopeKind.LegacyUnresolved"/> rather
    /// than <see cref="SagaMemoryScopeKind.Global"/>. Defaulting the other way would publish one
    /// Campaign's conclusions to every other Campaign on the strength of a missing row.
    /// </remarks>
    internal static (SagaMemoryScopeKind Kind, string? CampaignId) Classify(
        bool hasSession,
        long? bindingKindCode,
        string? boundCampaignId) =>
        (hasSession, bindingKindCode, boundCampaignId) switch
        {

            (false, _, _) => (SagaMemoryScopeKind.Global, null),

            (true, (long)SagaMemoryScopeKind.Global, _) => (SagaMemoryScopeKind.Global, null),

            (true, (long)SagaMemoryScopeKind.Campaign, { } owner) =>
                (SagaMemoryScopeKind.Campaign, CanonicalCampaignIdentity(owner)),

            _ => (SagaMemoryScopeKind.LegacyUnresolved, null),

        };

    /// <summary>
    /// Puts a bound Campaign identity into the one canonical form before it is handed on, so a memory's
    /// recorded scope never depends on the spelling the binding it was read from happens to hold.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes a Saga write independent of how far the version-5 sweep has drained, and
    /// that independence is a correctness requirement rather than a tidiness.</b> A schema step's DDL
    /// commits with its journal row and the sweep runs afterwards, in the transition coordinator's later
    /// passes - see <c>GrimoireSchemaInstaller.RunStepsAsync</c>, which returns <c>Incomplete</c> the
    /// moment a step declares a backfill. So version 5's guard on <c>saga_memories.CampaignId</c> is
    /// installed and enforcing while <c>session_campaign_bindings.CampaignId</c> may still hold the
    /// minority spelling on rows the sweep has not reached. Handing that value on verbatim aborted the
    /// insert on that guard, on every turn, for every Session still waiting - so a memory could not be
    /// written at all until the sweep finished.
    ///
    /// <para>It belongs here rather than at either writer, because this type exists to be the one place
    /// the decision is made: the live store and the version-two classification sweep both come through
    /// it, and canonicalizing downstream would mean doing it twice and eventually differently - which is
    /// the disagreement the class remarks above already refuse.</para>
    ///
    /// <para>A value that is not a recognizable identity is kept exactly as it was found, mirroring
    /// <c>CoreGrimoireSchemaDataInitializer.NormalizeCampaignId</c>: the binding column holds historical
    /// authority with no foreign key precisely so such a fact survives, and inventing an identity for it
    /// would be worse than reporting it. The guard refuses such a value and the sweep's count names it,
    /// which is the same treatment every other hand-edited identity in this family gets.</para>
    ///
    /// <para>Internal rather than private because the retirement suppression digest needs the same
    /// rendering and must not derive a second one. That digest takes the Campaign identity into its
    /// preimage, and its two ends read it from different places - the write path from this classifier,
    /// the release from the memory row the sweep has not necessarily reached - so a release that
    /// canonicalized differently, or not at all, would ask for a digest no retirement ever wrote.</para>
    /// </remarks>
    internal static string? CanonicalCampaignIdentity(string? campaignId) =>
        campaignId is null
            ? null
            : Guid.TryParse(campaignId, out Guid parsed)
                ? parsed.ToString("D").ToUpperInvariant()
                : campaignId;

    /// <summary>
    /// Reads one Session's binding and classifies it, inside the caller's transaction.
    /// </summary>
    /// <remarks>
    /// Inside the caller's transaction on purpose: a memory and the scope that decides who may recall it
    /// are one durable fact, and a scope read outside the insert could describe a binding that changed
    /// before the row landed.
    /// </remarks>
    internal static async Task<(SagaMemoryScopeKind Kind, string? CampaignId)> ResolveForSessionAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid? sessionId,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        if (sessionId is not { } owner)
        {

            return Classify(hasSession: false, null, null);

        }

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT BindingKindCode, CampaignId
            FROM session_campaign_bindings
            WHERE SessionId = @sessionId
            LIMIT 1;
            """;

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = "@sessionId";

        // The column is REFERENCES "Sessions"("Id") under an enforced foreign key, so it holds the
        // canonical spelling the object-relational writer gives the parent. Binding a bare ToString()
        // here matched no binding at all, and every memory then classified as LegacyUnresolved.
        parameter.Value = owner.ToString("D").ToUpperInvariant();

        command.Parameters.Add(parameter);

        await using DbDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Classify(
                hasSession: true,
                reader.IsDBNull(0) ? null : reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1))
            : Classify(hasSession: true, null, null);

    }

}
