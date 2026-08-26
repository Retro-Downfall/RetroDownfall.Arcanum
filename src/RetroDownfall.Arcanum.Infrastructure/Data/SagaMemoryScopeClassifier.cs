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
                (SagaMemoryScopeKind.Campaign, owner),

            _ => (SagaMemoryScopeKind.LegacyUnresolved, null),

        };

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

        parameter.Value = owner.ToString();

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
