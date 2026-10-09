using System.Data.Common;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

/// <summary>
/// One exact-current-source projection shared by recall and inspection. The survivor's later
/// lifecycle cannot revive an eliminated source; the source's own successor or pin can release it.
/// Every alias passed here is a code-owned SQL identifier, never caller text.
/// </summary>
internal static class LongRestRecallSql
{
    internal static async Task<bool> AvailableAsync(
        DbConnection connection,
        CancellationToken cancellationToken,
        DbTransaction? transaction = null) =>
        await GrimoireCoreSchemaVersion.ReadAsync(connection, cancellationToken, transaction).ConfigureAwait(false) >= 17;

    internal static string Suppressed(string memoryAlias) =>
        $"""
        EXISTS (
            SELECT 1
            {From(memoryAlias)}
        )
        """;

    internal static string Eligible(string memoryAlias, bool available) =>
        available ? $"NOT ({Suppressed(memoryAlias)})" : "1 = 1";

    internal static string Projection(string memoryAlias, bool available) =>
        available
            ? $"""
              , (SELECT lr_receipt.SurvivorMemoryId {From(memoryAlias)}) AS ConsolidatedIntoMemoryId
              , (SELECT lr_suppression.SurvivorVersionId {From(memoryAlias)}) AS ConsolidatedIntoVersionId
              , (SELECT lr_suppression.ReceiptId {From(memoryAlias)}) AS LongRestReceiptId
              """
            : " , NULL AS ConsolidatedIntoMemoryId, NULL AS ConsolidatedIntoVersionId, NULL AS LongRestReceiptId";

    private static string From(string memoryAlias) =>
        $"""
        FROM long_rest_suppressions lr_suppression
        JOIN annal_heads lr_head ON lr_head.CurrentVersionId = lr_suppression.SourceVersionId
            AND lr_head.SubjectStoreCode = 1
        JOIN annal_claims lr_claim ON lr_claim.ClaimId = lr_head.ClaimId AND lr_claim.SubjectStoreCode = 1
        JOIN long_rest_receipts lr_receipt ON lr_receipt.ReceiptId = lr_suppression.ReceiptId
        WHERE lr_claim.SubjectId = {memoryAlias}."Id" AND {memoryAlias}."PinnedAtUtc" IS NULL
        """;
}
