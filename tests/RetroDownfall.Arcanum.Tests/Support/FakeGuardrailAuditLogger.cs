using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Records every <see cref="LogAsync"/> call in-memory for assertions, without touching disk —
/// used by guardrails pipeline unit tests and provider-level integration tests to assert that a
/// blocked turn wrote a violation record (and to inspect its stage/type/redacted text).
/// </summary>
public sealed class FakeGuardrailAuditLogger : IGuardrailAuditLogger
{

    public List<GuardrailAuditRecord> Records { get; } = [];

    public Task LogAsync(GuardrailAuditRecord record, CancellationToken cancellationToken)
    {

        Records.Add(record);

        return Task.CompletedTask;

    }

    public Task<IReadOnlyList<GuardrailAuditRecord>> QueryAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? stage,
        string? violationType,
        string? sessionId,
        int limit,
        CancellationToken cancellationToken)
    {

        IEnumerable<GuardrailAuditRecord> query = Records.AsEnumerable().Reverse();

        if (stage is not null)
        {

            query = query.Where(r => string.Equals(r.Stage, stage, StringComparison.OrdinalIgnoreCase));

        }

        if (violationType is not null)
        {

            query = query.Where(r => string.Equals(r.ViolationType, violationType, StringComparison.OrdinalIgnoreCase));

        }

        if (sessionId is not null)
        {

            query = query.Where(r => string.Equals(r.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));

        }

        if (from.HasValue || to.HasValue)
        {

            query = query.Where(r =>
                DateTimeOffset.TryParse(r.Timestamp, out DateTimeOffset ts)
                && (!from.HasValue || ts >= from.Value)
                && (!to.HasValue || ts <= to.Value));

        }

        List<GuardrailAuditRecord> result = [.. query.Take(limit)];

        return Task.FromResult<IReadOnlyList<GuardrailAuditRecord>>(result);

    }

    public async Task<Result<AuditQueryPage<GuardrailAuditRecord>>> QueryPageAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? stage,
        string? violationType,
        string? sessionId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {

        int offset = DecodeOffset(cursor);

        if (offset < 0)
        {

            return Result<AuditQueryPage<GuardrailAuditRecord>>.Failure(
                new Error(ErrorCodes.Validation.InvalidQuery, "The audit cursor is invalid."));

        }

        IReadOnlyList<GuardrailAuditRecord> all = await QueryAsync(
            from,
            to,
            stage,
            violationType,
            sessionId,
            int.MaxValue,
            cancellationToken).ConfigureAwait(false);

        GuardrailAuditRecord[] records = [.. all.Skip(offset).Take(limit)];

        string? nextCursor = offset + records.Length < all.Count
            ? EncodeOffset(offset + records.Length)
            : null;

        return Result<AuditQueryPage<GuardrailAuditRecord>>.Success(
            new AuditQueryPage<GuardrailAuditRecord>(records, nextCursor));

    }

    private static string EncodeOffset(int offset) =>
        Convert.ToBase64String(BitConverter.GetBytes(offset));

    private static int DecodeOffset(string? cursor)
    {

        if (cursor is null)
        {

            return 0;

        }

        try
        {

            byte[] bytes = Convert.FromBase64String(cursor);

            return bytes.Length == sizeof(int)
                ? BitConverter.ToInt32(bytes)
                : -1;

        }
        catch (FormatException)
        {

            return -1;

        }

    }

}
