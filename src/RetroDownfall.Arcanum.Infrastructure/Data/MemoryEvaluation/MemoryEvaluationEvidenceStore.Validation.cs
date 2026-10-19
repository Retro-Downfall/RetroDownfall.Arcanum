using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;

internal sealed partial class MemoryEvaluationEvidenceStore
{
    private const int TerminalReserveBytes = 8192;

    private static readonly UTF8Encoding Utf8 = new(false, true);

    private static bool ValidRegistration(MemoryEvaluationRegistration? value, ImmutableArray<MemoryEvaluationSourceCoordinate> sources)
    {
        if (value is null || value.Ownership is null || value.EvaluationId == Guid.Empty || value.OperationId == Guid.Empty
            || value.Ownership.DatasetGeneration == Guid.Empty || !Scope(value.Ownership.Scope, value.Ownership.CampaignId)
            || !Instant(value.CreatedAtUtc) || value.SourceAsOfUtc is not { } asOf || !Instant(asOf) || asOf > value.CreatedAtUtc
            || value.PairCount is < MemoryEvaluationLimits.MinPairs or > MemoryEvaluationLimits.MaxPairs
            || value.CheckCount is < MemoryEvaluationLimits.MinChecks or > MemoryEvaluationLimits.MaxChecks
            || value.PolicyVersion != MemoryEvaluationLimits.PolicyVersion || value.FormatVersion != 1
            || sources.IsDefaultOrEmpty || sources.Length > MemoryEvaluationLimits.ManifestCoordinates
            || !new[] { value.RequestDigest, value.PlanDigest, value.WorkloadDigest, value.ProviderDigest, value.ModelDigest, value.OptionsDigest,
                value.PromptDigest, value.EvaluatorDigest, value.ManifestDigest }.All(digest => digest.IsValid)) return false;

        HashSet<(MemoryEvaluationSource, string, string, MemoryEvaluationCoordinateRole)> identities = [];

        Dictionary<(MemoryEvaluationSource, string, string), MemoryEvaluationSourceCoordinate> versions = [];

        int interventions = 0;

        foreach (MemoryEvaluationSourceCoordinate source in sources)
        {
            if (!ValidSource(source) || !identities.Add((source.Store, source.SubjectId, source.VersionId, source.Role))
                || source.CampaignId is { } campaign && campaign != value.Ownership.CampaignId
                || source.Generation is { } generation && generation != value.Ownership.DatasetGeneration) return false;
            var versionKey = (source.Store, source.SubjectId, source.VersionId);

            if (versions.TryGetValue(versionKey, out MemoryEvaluationSourceCoordinate? prior)
                && prior with { Role = source.Role, DependencyRelation = source.DependencyRelation } != source) return false;
            versions[versionKey] = source;

            if (source.Role == MemoryEvaluationCoordinateRole.Intervention) interventions++;
        }

        if (interventions is < MemoryEvaluationLimits.MinTargets or > MemoryEvaluationLimits.MaxTargets) return false;

        HashSet<(MemoryEvaluationSource, string, int)> ordinals = [];

        if (value.Dependencies.Length > MemoryEvaluationLimits.ManifestCoordinates * MemoryEvaluationLimits.ManifestCoordinates) return false;

        foreach (MemoryEvaluationDependencyEdge edge in value.Dependencies)
        {
            if (edge is null || !Enum.IsDefined(edge.Store) || !Enum.IsDefined(edge.Relation) || edge.Ordinal < 0
                || !Opaque(edge.DependentVersionId) || !Opaque(edge.DependencyVersionId) || edge.DependentVersionId == edge.DependencyVersionId
                || !ordinals.Add((edge.Store, edge.DependentVersionId, edge.Ordinal))
                || !HasAnnalVersionBinding(sources, edge.DependentVersionId, edge.Store)
                || !HasAnnalVersionBinding(sources, edge.DependencyVersionId)) return false;
        }

        return MemoryEvaluationDigests.SourceManifest(sources, value.Dependencies) == value.ManifestDigest;
    }

    // An edge's Store names its dependent. Annals version IDs are shared across Saga/Lexicon,
    // so the target's unique owning binding must be resolved independently, never through Covenant.
    private static bool HasAnnalVersionBinding(ImmutableArray<MemoryEvaluationSourceCoordinate> sources, string versionId, MemoryEvaluationSource? owningStore = null)
    {
        var bindings = sources.Where(source => source.Store is MemoryEvaluationSource.Saga or MemoryEvaluationSource.Lexicon && source.VersionId == versionId)
            .Select(source => (source.Store, source.SubjectId, source.ClaimId)).Distinct().Take(2).ToArray();

        return bindings.Length == 1 && (owningStore is null || bindings[0].Store == owningStore);
    }

    private static bool ValidSource(MemoryEvaluationSourceCoordinate? source)
    {
        if (source is null || !Enum.IsDefined(source.Role) || !Enum.IsDefined(source.Store) || !Enum.IsDefined(source.Lifecycle)
            || !Enum.IsDefined(source.Sensitivity) || !Scope(source.Scope, source.CampaignId) || !source.ContentDigest.IsValid || source.Revision < 1
            || !Opaque(source.SubjectId) || !Opaque(source.VersionId) || source.ClaimId is { } claim && !Opaque(claim)
            || source.DependencyRelation is { } relation && !Enum.IsDefined(relation)
            || source.Origin is { } origin && !Enum.IsDefined(origin)
            || source.CanonicalOwnerSessionId is { } owner && !CanonicalGuid(owner, "D", true)
            || source.SourceSessionId is { } author && !AuthorSession(author)
            || new[] { source.ValidFromUtc, source.ValidToUtc, source.RecordedAtUtc, source.RecordedUntilUtc }.Any(time => time is { } instant && !Instant(instant))
            || source.ValidFromUtc is { } from && source.ValidToUtc is { } until && from >= until
            || source.RecordedAtUtc is { } recorded && source.RecordedUntilUtc is { } ended && recorded >= ended) return false;

        if (source.Store == MemoryEvaluationSource.Covenant)
        {
            return CanonicalGuid(source.SubjectId, "D", false) && CanonicalGuid(source.VersionId, "D", false)
                && source.ClaimId is null && source.Origin is null && source.HashFormat == CovenantCompiler.RendererPolicyVersion
                && source.Generation is { } generation && generation != Guid.Empty
                && source.Lane is { } lane && Enum.IsDefined(lane) && source.Placement is { } placement && Enum.IsDefined(placement)
                && source.CovenantOrigin is { } covenantOrigin && Enum.IsDefined(covenantOrigin)
                && source.Sensitivity == ContentSensitivity.CovenantDerived
                && source.ValidFromUtc is null && source.ValidToUtc is null && source.RecordedAtUtc is null && source.RecordedUntilUtc is null;
        }

        return (source.Store != MemoryEvaluationSource.Lexicon || CanonicalGuid(source.SubjectId, "N", false))
            && source.ClaimId is not null && source.Origin is not null && source.ValidFromUtc is not null && source.RecordedAtUtc is not null
            && source.HashFormat is 1 or 2 && (source.Store != MemoryEvaluationSource.Saga || source.HashFormat == 1)
            && source.Generation is null && source.Lane is null && source.Placement is null && source.CovenantOrigin is null;
    }

    private static bool ValidTerminal(MemoryEvaluationTerminalReport? report)
    {
        if (report is null || report.Registration is null || report.Aggregate is null || report.Decision is null
            || !Instant(report.CompletedAtUtc) || report.CompletedAtUtc < report.Registration.CreatedAtUtc
            || report.State is not MemoryEvaluationState.Completed and not MemoryEvaluationState.Interrupted and not MemoryEvaluationState.Cancelled
            || report.InputTokens < 0 || report.OutputTokens < 0 || report.Cost < 0 || report.ElapsedMilliseconds < 0
            || !report.Aggregate.ResponseEvidenceDigest.IsValid || !Enum.IsDefined(report.Decision.Outcome) || !Enum.IsDefined(report.Decision.Reason)
            || !report.Dependencies.SequenceEqual(report.Registration.Dependencies)
            || MemoryEvaluationPolicy.Classify(report.Aggregate, report.Registration.PairCount) != report.Decision
            || report.State == MemoryEvaluationState.Interrupted && report.Decision.Reason != MemoryEvaluationReason.Interrupted
            || report.State == MemoryEvaluationState.Cancelled && report.Decision.Reason != MemoryEvaluationReason.Cancelled) return false;

        MemoryEvaluationAggregate aggregate = report.Aggregate;

        int pairs = report.Registration.PairCount;

        if (new[] { aggregate.CompletedPairs, aggregate.ValidPairs, aggregate.InvalidPairs, aggregate.ImprovedPairs, aggregate.RegressedPairs, aggregate.EquivalentPairs, aggregate.MixedPairs }.Any(count => count < 0 || count > pairs)
            || (long)aggregate.ValidPairs + aggregate.InvalidPairs != aggregate.CompletedPairs
            || (long)aggregate.ImprovedPairs + aggregate.RegressedPairs + aggregate.EquivalentPairs + aggregate.MixedPairs != aggregate.ValidPairs
            || !Enum.IsDefined(aggregate.FailureReason)) return false;
        if (aggregate.Checks.Length != report.Registration.CheckCount && !(aggregate.ValidPairs == 0 && aggregate.Checks.IsEmpty)) return false;

        HashSet<int> indices = [];

        foreach (MemoryEvaluationCheckAggregate check in aggregate.Checks)
        {
            if (check is null || check.CheckIndex < 0 || check.CheckIndex >= report.Registration.CheckCount || !indices.Add(check.CheckIndex)
                || !Enum.IsDefined(check.Kind) || !check.CheckPolicyDigest.IsValid
                || check.FailFail < 0 || check.FailPass < 0 || check.PassFail < 0 || check.PassPass < 0
                || (long)check.FailFail + check.FailPass + check.PassFail + check.PassPass != aggregate.ValidPairs
                || check.FailPass > aggregate.ImprovedPairs + aggregate.MixedPairs
                || check.PassFail > aggregate.RegressedPairs + aggregate.MixedPairs) return false;
        }

        return aggregate.Checks.Sum(check => check.FailPass) >= aggregate.ImprovedPairs + aggregate.MixedPairs
            && aggregate.Checks.Sum(check => check.PassFail) >= aggregate.RegressedPairs + aggregate.MixedPairs;
    }

    private static bool Scope(MemoryEvaluationScopeKind scope, Guid? campaign) => scope switch
    {
        MemoryEvaluationScopeKind.Global => campaign is null,
        MemoryEvaluationScopeKind.Campaign => campaign is { } id && id != Guid.Empty,
        _ => false
    };

    private static bool Instant(DateTimeOffset value) => value.Offset == TimeSpan.Zero;

    private static bool Opaque(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) return false;

        try
        {
            return Utf8.GetByteCount(value) <= MemoryEvaluationLimits.EvidenceBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool AuthorSession(string value) => value == value.Trim() && Guid.TryParse(value, out Guid id) && id != Guid.Empty;

    private static bool CanonicalGuid(string value, string format, bool upper) => Guid.TryParseExact(value, format, out Guid id) && id != Guid.Empty
        && value == (upper ? id.ToString(format).ToUpperInvariant() : id.ToString(format));

    /// <summary>Immutable root lookup used by erasure plans; child removal never destroys this selection.</summary>
    private static ImmutableArray<string> ErasureKeys(ImmutableArray<MemoryEvaluationSourceCoordinate> sources)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);

        foreach (MemoryEvaluationSourceCoordinate source in sources)
        {
            string store = ((int)source.Store).ToString(System.Globalization.CultureInfo.InvariantCulture);

            keys.Add(store + ":subject:" + source.SubjectId);

            keys.Add(store + ":version:" + source.VersionId);

            if (source.ClaimId is { } claim) keys.Add(store + ":claim:" + claim);

            if (source.CanonicalOwnerSessionId is { } owner) keys.Add("session:" + Guid.Parse(owner).ToString("N"));

            if (source.SourceSessionId is { } author) keys.Add("session:" + Guid.Parse(author).ToString("N"));
        }

        return [.. keys.Order(StringComparer.Ordinal)];
    }

    private static int PreparedSize(MemoryEvaluationRegistration registration, ImmutableArray<MemoryEvaluationSourceCoordinate> sources, string registrationJson, string keys, string[] coordinates)
    {
        // Count every normalized value as UTF-8, including duplicated SQL keys/times and framing.
        long size = Utf8.GetByteCount(registrationJson) + Utf8.GetByteCount(keys) + 32 * 3 + 28 * 2 + 32 + 64;

        foreach (MemoryEvaluationSourceCoordinate source in sources)
            size += 32 + 32 + Utf8.GetByteCount(source.SubjectId) + Utf8.GetByteCount(source.VersionId);
        size += coordinates.Sum(json => (long)Utf8.GetByteCount(json));

        return checked((int)size);
    }

    private static bool FitsTerminalBudget(MemoryEvaluationRegistration registration, ImmutableArray<MemoryEvaluationSourceCoordinate> sources, int preparedBytes)
    {
        if (preparedBytes > MemoryEvaluationLimits.EvidenceBytes - TerminalReserveBytes) return false;

        // Upper bound the API projection too: it repeats root/manifest inside the terminal report.
        // All terminal numeric values, strings, nullable usage and check arrays are bounded here.
        var digest = new CovenantDigest(new byte[32]);

        var checks = Enumerable.Range(0, registration.CheckCount).Select(index => new MemoryEvaluationCheckAggregate(index, MemoryEvaluationCheckKind.JsonSchema, digest, 16, 16, 16, 16)).ToImmutableArray();

        var aggregate = new MemoryEvaluationAggregate(16, 16, 16, 16, 16, 16, 16, checks, digest, MemoryEvaluationReason.StaleConfiguration);

        var report = new MemoryEvaluationTerminalReport(registration, DateTimeOffset.MaxValue, MemoryEvaluationState.Interrupted,
            new(MemoryEvaluationOutcome.InsufficientEvidence, MemoryEvaluationReason.StaleConfiguration), aggregate, sources,
            long.MaxValue, long.MaxValue, decimal.MaxValue, long.MaxValue, registration.Dependencies);

        var projection = new MemoryEvaluationStoredEvidence(registration, sources, report, digest);

        // Margin covers the longest closed enum spellings and decimal fractional punctuation.
        return JsonSerializer.SerializeToUtf8Bytes(projection, GrimoireJsonContext.Default.MemoryEvaluationStoredEvidence).Length + 256 <= MemoryEvaluationLimits.EvidenceBytes;
    }
}
