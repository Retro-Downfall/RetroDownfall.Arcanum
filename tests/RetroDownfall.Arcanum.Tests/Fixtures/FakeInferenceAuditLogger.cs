using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>
/// Records every <see cref="LogAsync"/> call in-memory for assertions, without touching disk —
/// used wherever a test constructs <c>WizardIntelligenceProvider</c> directly (bypassing DI) and
/// needs an <see cref="IInferenceAuditLogger"/> to satisfy the constructor.
/// </summary>
public sealed class FakeInferenceAuditLogger : IInferenceAuditLogger
{

    public List<InferenceAuditRecord> Records { get; } = [];

    public Task LogAsync(InferenceAuditRecord record, CancellationToken cancellationToken)
    {

        Records.Add(record);

        return Task.CompletedTask;

    }

    public Task<IReadOnlyList<InferenceAuditRecord>> QueryAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? model,
        string? sessionId,
        int limit,
        CancellationToken cancellationToken)
    {

        IEnumerable<InferenceAuditRecord> query = Records.AsEnumerable().Reverse();

        if (model is not null)
        {

            query = query.Where(r => string.Equals(r.Model, model, StringComparison.OrdinalIgnoreCase));

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

        List<InferenceAuditRecord> result = [.. query.Take(limit)];

        return Task.FromResult<IReadOnlyList<InferenceAuditRecord>>(result);

    }

    public async Task<Result<AuditQueryPage<InferenceAuditRecord>>> QueryPageAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? model,
        string? sessionId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {

        int offset = DecodeOffset(cursor);

        if (offset < 0)
        {

            return Result<AuditQueryPage<InferenceAuditRecord>>.Failure(
                new Error(ErrorCodes.Validation.InvalidQuery, "The audit cursor is invalid."));

        }

        IReadOnlyList<InferenceAuditRecord> all = await QueryAsync(
            from,
            to,
            model,
            sessionId,
            int.MaxValue,
            cancellationToken).ConfigureAwait(false);

        InferenceAuditRecord[] records = [.. all.Skip(offset).Take(limit)];

        string? nextCursor = offset + records.Length < all.Count
            ? EncodeOffset(offset + records.Length)
            : null;

        return Result<AuditQueryPage<InferenceAuditRecord>>.Success(
            new AuditQueryPage<InferenceAuditRecord>(records, nextCursor));

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
