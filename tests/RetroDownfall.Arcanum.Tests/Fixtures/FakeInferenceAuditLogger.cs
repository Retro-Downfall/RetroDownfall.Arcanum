using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

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

}
