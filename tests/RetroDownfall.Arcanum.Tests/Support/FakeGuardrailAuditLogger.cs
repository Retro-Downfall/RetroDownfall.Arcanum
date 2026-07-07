using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

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

}
