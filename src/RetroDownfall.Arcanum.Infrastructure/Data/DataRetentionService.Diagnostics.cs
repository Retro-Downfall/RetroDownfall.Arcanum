using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class DataRetentionService
{
    private async ValueTask<string?> ReadPruneLabelRefusalAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        Result unlabeled = await labeledArtifactGuard.EnsureUnlabeledAsync(kind, artifactId, cancellationToken).ConfigureAwait(false);

        return unlabeled.IsFailure ? ClosedPruneLabelCause(unlabeled.Error) : null;
    }

    private static string ClosedPruneLabelCause(Error error) => error.Code is ErrorCodes.Covenant.ForbiddenAuthority
        ? ErrorCodes.Covenant.ForbiddenAuthority : ErrorCodes.Covenant.Unavailable;

    private static string PruneLabelMessage(string cause) => cause is ErrorCodes.Covenant.ForbiddenAuthority
        ? "Covenant-labelled data is protected from ordinary retention; use the supported Covenant erasure workflow."
        : "Sensitivity labels could not be verified; this candidate was preserved. Restore label-store availability and request a new dry-run.";

    public async Task<Result<DataRetentionDiagnosticPageAdmission>> ReadPruneDiagnosticsAdmissionAsync(
        DataRetentionPruneDiagnosticsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.Request is not { Operation: DataRetentionOperation.Prune }
            || string.IsNullOrWhiteSpace(request.ExpectedPlanId) || request.ExpectedPlanId.Length != 64
            || !request.ExpectedPlanId.All(static character => char.IsAsciiHexDigit(character))
                    || string.IsNullOrWhiteSpace(request.ExpectedSnapshotId) || request.ExpectedSnapshotId.Length != 64
                    || !request.ExpectedSnapshotId.All(static character => char.IsAsciiHexDigit(character))
                    || request.ObservedAt == default || request.Limit is < 1 or > 200)
        {
            return Result<DataRetentionDiagnosticPageAdmission>.Failure(new Error(ErrorCodes.Data.InvalidRequest,
                "Supply a prune preview's complete PlanId, diagnostic SnapshotId, and GeneratedAt, with a detail limit from 1 through 200."));
        }

        string context = DiagnosticContext(request);

        if (!TryReadDiagnosticContinuation(request.Continuation, context, out long offset))
        {
            return Result<DataRetentionDiagnosticPageAdmission>.Failure(new Error(ErrorCodes.Data.InvalidRequest,
                "The diagnostic continuation does not match this prune preview; request a new preview."));
        }

        Result<DataRetentionPlanAdmission> result = await PlanAdmissionCoreAsync(request.Request, cancellationToken,
            observedAt: request.ObservedAt, diagnosticWindow: new(offset, request.Limit)).ConfigureAwait(false);

        if (result.IsFailure) return Result<DataRetentionDiagnosticPageAdmission>.Failure(result.Error);

        DataRetentionPlanAdmission admission = result.Value;

        bool transferred = false;

        try
        {
            DataRetentionPlan plan = admission.Plan;

            if (!string.Equals(request.ExpectedPlanId, plan.PlanId, StringComparison.Ordinal)
                || plan.Diagnostics is not { } diagnostics
                || !string.Equals(request.ExpectedSnapshotId, diagnostics.SnapshotId, StringComparison.Ordinal))
            {
                return Result<DataRetentionDiagnosticPageAdmission>.Failure(new Error(ErrorCodes.Data.PlanChanged,
                    "The complete prune diagnostic snapshot changed; request a new dry-run before reading details."));
            }

            long total = diagnostics.BlockerCount + diagnostics.ConflictCount;

            if (offset > total || offset == total && offset != 0)
            {
                return Result<DataRetentionDiagnosticPageAdmission>.Failure(new Error(ErrorCodes.Data.InvalidRequest,
                    "The diagnostic continuation is outside this complete snapshot; request a new preview."));
            }

            DataRetentionDiagnosticDetail[] details = [..
                plan.Blockers.Select(static blocker => new DataRetentionDiagnosticDetail(true, blocker.DataClass,
                    blocker.ResourceId, blocker.ReasonCode, blocker.Message, blocker.HoldingSessionId)),
                .. plan.Conflicts.Select(static conflict => new DataRetentionDiagnosticDetail(false, null,
                    conflict.ResourceId, conflict.Code, conflict.Message, conflict.HoldingSessionId))];

            long next = offset + details.Length;

            cancellationToken.ThrowIfCancellationRequested();

            transferred = true;

            return Result<DataRetentionDiagnosticPageAdmission>.Success(new(new(plan.PlanId, plan.GeneratedAt,
                total, diagnostics.BlockerCount, diagnostics.ConflictCount, details,
                next < total ? DiagnosticContinuation(context, next) : null), admission.ReadLease));
        }
        finally
        {
            if (!transferred && admission.ReadLease is not null)
            {
                await admission.ReadLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string DiagnosticContext(DataRetentionPruneDiagnosticsRequest request)
    {
        using RetentionPlanHasher hash = new();

        hash.Append("PruneDiagnostics/1|").Append(request.ExpectedPlanId).Append('|')
            .Append(request.ExpectedSnapshotId).Append('|')
            .Append(UtcInstantText.Format(request.ObservedAt)).Append('|').Append((int)request.Request.Operation)
            .Append('|').Append(request.Request.TargetId?.ToString("N") ?? string.Empty).Append('|')
            .Append(request.Request.MemoryScope is null ? string.Empty : ((int)request.Request.MemoryScope.Value).ToString(CultureInfo.InvariantCulture));

        AppendDiagnosticWorkspace(hash, request.Request.Workspace);

        return Convert.ToHexString(hash.Finish());
    }

    private static void AppendDiagnosticWorkspace(RetentionPlanHasher hash, DataRetentionWorkspaceBinding? workspace)
    {
        hash.Append(workspace is null ? "|w:0" : "|w:1");

        if (workspace is not null)
        {
            hash.Append('|').Append(workspace.CampaignId.ToString("N")).Append('|')
                .Append(workspace.WorkspaceRoot?.Length ?? -1).Append(':').Append(workspace.WorkspaceRoot ?? string.Empty);
        }
    }

    private static string DiagnosticContinuation(string context, long offset) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes("1|" + context + "|" + offset.ToString(CultureInfo.InvariantCulture)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryReadDiagnosticContinuation(string? text, string context, out long offset)
    {
        offset = 0;

        if (text is null) return true;

        if (text.Length is 0 or > 160) return false;

        try
        {
            string encoded = text.Replace('-', '+').Replace('_', '/');

            string[] fields = Encoding.ASCII.GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='))).Split('|');

            return fields is ["1", var digest, var ordinal] && digest == context
                && long.TryParse(ordinal, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out offset)
                && offset >= 0 && DiagnosticContinuation(context, offset) == text;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private readonly record struct PruneDiagnosticWindow(long Offset, int Limit);

    private static DataRetentionDiagnostics BuildDiagnosticSummary(
        RetentionDiagnosticCollection<DataRetentionBlocker> blockers,
        RetentionDiagnosticCollection<DataRetentionConflict> conflicts,
        string planId,
        DataRetentionRequest request,
        DateTimeOffset observedAt,
        int displayedBlockers,
        int displayedConflicts)
    {
        DataRetentionReasonCount[] blockerReasons = [.. blockers.Facts
            .GroupBy(static fact => (fact.DataClass, fact.Code))
            .OrderBy(static group => group.Key.DataClass)
            .ThenBy(static group => group.Key.Code, StringComparer.Ordinal)
            .Select(static group => new DataRetentionReasonCount(group.Key.DataClass, group.Key.Code, group.LongCount()))];

        DataRetentionReasonCount[] conflictReasons = [.. conflicts.Facts
            .GroupBy(static fact => fact.Code)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => new DataRetentionReasonCount(null, group.Key, group.LongCount()))];

        Dictionary<Guid, (long Blockers, long Conflicts)> holders = [];

        foreach (DiagnosticFact fact in blockers.Facts)
        {
            if (fact.Holder is not Guid holder) continue;

            holders.TryGetValue(holder, out var count);

            holders[holder] = (count.Blockers + 1, count.Conflicts);
        }

        foreach (DiagnosticFact fact in conflicts.Facts)
        {
            if (fact.Holder is not Guid holder) continue;

            holders.TryGetValue(holder, out var count);

            holders[holder] = (count.Blockers, count.Conflicts + 1);
        }

        using RetentionPlanHasher snapshot = new();

        snapshot.Append("PruneDiagnosticSnapshot/1|").Append(planId).Append('|').Append(UtcInstantText.Format(observedAt));

        AppendDiagnosticWorkspace(snapshot, request.Workspace);

        foreach (DiagnosticFact fact in blockers.Facts.Concat(conflicts.Facts)
                     .OrderBy(static fact => fact.DataClass)
                     .ThenBy(static fact => fact.ResourceId, StringComparer.Ordinal)
                     .ThenBy(static fact => fact.Code, StringComparer.Ordinal))
        {
            snapshot.Append('|').Append(fact.DataClass is { } dataClass ? (int)dataClass : -1)
                .Append(':').Append(fact.ResourceId.Length).Append(':').Append(fact.ResourceId)
                .Append(':').Append(fact.Code.Length).Append(':').Append(fact.Code)
                .Append(':').Append(fact.Holder?.ToString("N") ?? string.Empty);
        }

        return new(blockers.Count, conflicts.Count, blockerReasons, conflictReasons, holders.Count,
            [.. holders.OrderBy(static pair => pair.Key).Take(20)
                .Select(static pair => new DataRetentionHoldingSession(pair.Key, pair.Value.Blockers, pair.Value.Conflicts))],
            Convert.ToHexString(snapshot.Finish()),
            blockers.Count == displayedBlockers && conflicts.Count == displayedConflicts,
            blockers.Count - displayedBlockers, conflicts.Count - displayedConflicts,
            Math.Max(0, holders.Count - 20), observedAt);
    }

    private readonly record struct DiagnosticFact(
        RetentionDataClass? DataClass,
        string ResourceId,
        string Code,
        string Message,
        Guid? Holder);

    // Complete compact facts are authority inputs; materialized DTOs are display only. No observed
    // sample, holder summary or count can grant deletion authority or replace the complete digest.
    private sealed class RetentionDiagnosticCollection<T>(bool complete, PruneDiagnosticWindow? window = null)
    {
        private readonly Dictionary<(RetentionDataClass? DataClass, string ResourceId, string Code), DiagnosticFact> _facts = [];

        internal bool Complete { get; } = complete;

        internal PruneDiagnosticWindow? Window { get; } = window;

        internal HashSet<Guid> ObservedSessions { get; } = [];

        internal IEnumerable<DiagnosticFact> Facts => _facts.Values;

        public int Count => _facts.Count;

        public void Add(T item) => AddForSession(item, null);

        internal void AddForSession(T item, Guid? holder)
        {
            DiagnosticFact fact = item switch
            {
                DataRetentionBlocker blocker => new(blocker.DataClass, blocker.ResourceId, blocker.ReasonCode, blocker.Message, holder),
                DataRetentionConflict conflict => new(null, conflict.ResourceId, conflict.Code, conflict.Message, holder),
                _ => throw new InvalidOperationException("Unsupported retention diagnostic kind."),
            };

            _facts.TryAdd((fact.DataClass, fact.ResourceId, fact.Code), fact);
        }

        internal IOrderedEnumerable<DiagnosticFact> OrderedFacts() => _facts.Values
            .OrderBy(static fact => fact.DataClass)
            .ThenBy(static fact => fact.DataClass.HasValue ? fact.ResourceId : fact.Code, StringComparer.Ordinal)
            .ThenBy(static fact => fact.DataClass.HasValue ? fact.Code : fact.ResourceId, StringComparer.Ordinal);

        internal void AddRange(IEnumerable<T> items)
        {
            foreach (T item in items) Add(item);
        }
    }

    private static T[] MaterializeDiagnostics<T>(IEnumerable<DiagnosticFact> facts, long offset, int limit)
    {
        List<T> result = [];

        if (limit <= 0) return [];

        foreach (DiagnosticFact fact in facts)
        {
            if (offset > 0)
            {
                offset--;

                continue;
            }

            result.Add(fact.DataClass is RetentionDataClass dataClass
                ? (T)(object)new DataRetentionBlocker(dataClass, fact.ResourceId, fact.Code, fact.Message, fact.Holder)
                : (T)(object)new DataRetentionConflict(fact.Code, fact.ResourceId, fact.Message, fact.Holder));

            if (--limit == 0) break;
        }

        return [.. result];
    }

    private static void AddDiagnosticForSession<T>(RetentionDiagnosticCollection<T> collection, T item, Guid? holder) =>
        collection.AddForSession(item, holder);

    private sealed class RetentionPlanHasher : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        internal RetentionPlanHasher Append(string value)
        {
            // Each existing canonical component is complete; an encoder preserves non-BMP scalars
            // while streaming bounded UTF-8 chunks instead of retaining a corpus-sized string.
            Encoder encoder = Encoding.UTF8.GetEncoder();

            ReadOnlySpan<char> input = value.AsSpan();

            Span<byte> bytes = stackalloc byte[1024];

            do
            {
                encoder.Convert(input, bytes, true, out int charsUsed, out int bytesUsed, out bool completed);

                _hash.AppendData(bytes[..bytesUsed]);

                input = input[charsUsed..];

                if (completed) break;
            }

            while (true);

            return this;
        }

        internal RetentionPlanHasher Append(char value) => Append(value.ToString());

        internal RetentionPlanHasher Append(long value) => Append(value.ToString(CultureInfo.InvariantCulture));

        internal byte[] Finish() => _hash.GetHashAndReset();

        public void Dispose() => _hash.Dispose();
    }
}
