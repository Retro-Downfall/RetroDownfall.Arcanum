using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// In-memory <see cref="ILexiconService"/> for MCP / pipeline tests that do not exercise the real
/// Grimoire. Mirrors the real service's name-normalization and append semantics closely enough for
/// tool-call assertions.
/// </summary>
public sealed class FakeLexiconService : ILexiconService, ILexiconCurationService
{

    /// <summary>
    /// Keyed by tier and then name, the way the real table's unique index now is: one name may exist
    /// once per scope, so a fake keyed by name alone would let a Campaign write silently overwrite the
    /// global entity and hide exactly the bug these tests are meant to catch.
    /// </summary>
    private readonly Dictionary<(string Scope, string Name), LexiconEntryDto> _entries = [];

    public Task<Result<LexiconEntryDto>> UpsertAsync(
        string name,
        string? type,
        IReadOnlyList<string> facts,
        LexiconScope scope,
        CancellationToken cancellationToken = default)
    {

        string trimmedName = name.Trim();

        if (trimmedName.Length == 0)
        {
            return Task.FromResult(Result<LexiconEntryDto>.Failure(new Error(ErrorCodes.Lexicon.InvalidName, "name required")));
        }

        string normalized = trimmedName.ToUpperInvariant();

        List<string> incoming = facts.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList();

        if (incoming.Count == 0)
        {
            return Task.FromResult(Result<LexiconEntryDto>.Failure(new Error(ErrorCodes.Lexicon.InvalidFact, "facts required")));
        }

        if (_entries.TryGetValue((scope.Key, normalized), out LexiconEntryDto? existing))
        {
            List<string> merged = [.. existing.Facts, .. incoming];

            string resolvedType = string.IsNullOrWhiteSpace(type) ? existing.Type : type!;

            LexiconEntryDto updated = existing with { Type = resolvedType, Facts = merged.ToArray(), UpdatedAt = DateTimeOffset.UtcNow };

            _entries[(scope.Key, normalized)] = updated;

            return Task.FromResult(Result<LexiconEntryDto>.Success(updated));
        }

        LexiconEntryDto entry = new(
            Guid.NewGuid(),
            trimmedName,
            string.IsNullOrWhiteSpace(type) ? LexiconLimits.DefaultType : type!,
            incoming.ToArray(),
            DateTimeOffset.UtcNow,
            FactProvenance: null,
            ScopeCampaignId: scope.CampaignId);

        _entries[(scope.Key, normalized)] = entry;

        return Task.FromResult(Result<LexiconEntryDto>.Success(entry));
    }

    public Task<Result<Guid?>> FindAllLifecycleIdentityForDeletionAsync(
        string name, LexiconScope scope, CancellationToken cancellationToken = default)
    {
        _ = _entries.TryGetValue((scope.Key, name.Trim().ToUpperInvariant()), out LexiconEntryDto? entry);

        return Task.FromResult(Result<Guid?>.Success(entry?.Id));
    }

    /// <summary>This fake never retires or pins, so every entry it holds is one an agent may delete.</summary>
    public Task<Result<LexiconAgentDeletionTarget?>> FindAgentDeletionTargetAsync(
        string name, LexiconScope scope, CancellationToken cancellationToken = default)
    {
        _ = _entries.TryGetValue((scope.Key, name.Trim().ToUpperInvariant()), out LexiconEntryDto? entry);

        return Task.FromResult(Result<LexiconAgentDeletionTarget?>.Success(entry is null ? null : new(entry.Id, false, false)));
    }

    public Task<Result<bool>> DeleteByNameAsync(
        string name,
        LexiconScope scope,
        CancellationToken cancellationToken = default)
    {

        string normalized = name.Trim().ToUpperInvariant();

        bool removed = _entries.Remove((scope.Key, normalized));

        return Task.FromResult(Result<bool>.Success(removed));
    }

    public Task<Result<bool>> DeleteByNameAsync(
        string name,
        LexiconScope scope,
        LexiconDeletionOrigin origin,
        CancellationToken cancellationToken = default) =>
        DeleteByNameAsync(name, scope, cancellationToken);

    public Task<Result<IReadOnlyList<LexiconEntryDto>>> MatchEntitiesAsync(
        IReadOnlyList<string> entities,
        int limit,
        LexiconScope scope,
        CancellationToken cancellationToken = default)
    {

        // The Campaign tier first, then whatever global names it has not answered: the same shadowing
        // the real service applies, so a test using this fake cannot pass on a merge the real one
        // refuses to perform.
        HashSet<string> shadowed = new(StringComparer.Ordinal);

        List<LexiconEntryDto> results = [];

        foreach (string scopeKey in scope.IsGlobal ? [LexiconScope.Global.Key] : new[] { scope.Key, LexiconScope.Global.Key })
        {

            foreach (((string Scope, string Name) key, LexiconEntryDto entry) in _entries)
            {

                if (!string.Equals(key.Scope, scopeKey, StringComparison.Ordinal)
                    || !shadowed.Add(key.Name)
                    || results.Count >= limit)
                {
                    continue;
                }

                results.Add(entry);

            }

        }

        return Task.FromResult<Result<IReadOnlyList<LexiconEntryDto>>>(
            Result<IReadOnlyList<LexiconEntryDto>>.Success(results));
    }

    public Task<Result<LexiconEntryDto?>> GetByNameAsync(
        string name,
        LexiconScope scope,
        CancellationToken cancellationToken = default)
    {

        string normalized = name.Trim().ToUpperInvariant();

        if (!_entries.TryGetValue((scope.Key, normalized), out LexiconEntryDto? entry) && !scope.IsGlobal)
        {

            _ = _entries.TryGetValue((LexiconScope.Global.Key, normalized), out entry);

        }

        return Task.FromResult(Result<LexiconEntryDto?>.Success(entry));
    }

    public Task<Result<LexiconEntryDto?>> GetByNameInScopeAsync(
        string name,
        LexiconScope scope,
        CancellationToken cancellationToken = default)
    {

        _ = _entries.TryGetValue((scope.Key, name.Trim().ToUpperInvariant()), out LexiconEntryDto? entry);

        return Task.FromResult(Result<LexiconEntryDto?>.Success(entry));
    }

    public Task<Result<LexiconInspectionResult<IReadOnlyList<LexiconEntryDto>>>> ListInspectionAsync(
        ICovenantSnapshotReadLease? readLease,
        CancellationToken cancellationToken = default)
    {

        IReadOnlyList<LexiconEntryDto> entries = _entries.Values
            .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.ScopeCampaignId?.ToString("D") ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Id.ToString("N"), StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(
            Result<LexiconInspectionResult<IReadOnlyList<LexiconEntryDto>>>.Success(new(entries, false)));

    }

    public async Task<Result<LexiconInspectionResult<IReadOnlyList<LexiconEntryDto>>>> SearchInspectionAsync(
        string? query, int? limit, ICovenantSnapshotReadLease? readLease, CancellationToken cancellationToken = default)
    {
        if (limit < 0)
        {
            return new Error(ErrorCodes.Validation.InvalidQuery, "Lexicon inspection limit must be non-negative or omitted.");
        }

        var listed = await ListInspectionAsync(readLease, cancellationToken);

        string? trimmed = query?.Trim();

        IReadOnlyList<LexiconEntryDto> entries = listed.Value.Value.Where(entry => string.IsNullOrEmpty(trimmed)
            || entry.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || entry.Type.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || entry.Facts.Any(fact => fact.Contains(trimmed, StringComparison.OrdinalIgnoreCase)))
            .Take(limit ?? int.MaxValue).ToArray();

        return Result<LexiconInspectionResult<IReadOnlyList<LexiconEntryDto>>>.Success(new(entries, false));
    }

    public Task<Result<LexiconInspectionResult<LexiconInspectionCounts>>> CountInspectionAsync(
        ICovenantSnapshotReadLease? readLease, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<LexiconInspectionResult<LexiconInspectionCounts>>.Success(new(
            new(_entries.Count, _entries.Values.Count(entry => entry.Eligibility == LexiconRetrievalEligibility.Eligible)), false)));

    public Task<Result<LexiconInspectionResult<LexiconEntryDetail>>> ShowExactAsync(
        LexiconCurationScope scope, string name, ICovenantSnapshotReadLease? readLease,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<LexiconInspectionResult<LexiconEntryDetail>>.Failure(
            new Error(ErrorCodes.Lexicon.SearchFailed, "This fake does not provide exact evidence.")));

    public async Task<Result<LexiconInspectionResult<LexiconEntryDetail>>> ShowEffectiveAsync(
        LexiconCurationScope requestedScope, string name, ICovenantSnapshotReadLease? installationReadLease,
        CancellationToken cancellationToken = default)
    {
        // This compatibility fake owns no label ledger and must never stand in for protected inspection.
        if (installationReadLease is not null)
        {
            return new Error(ErrorCodes.Covenant.ForbiddenAuthority, "This fake cannot verify protected inspection.");
        }

        Result<LexiconEntryDto?> found = await GetByNameAsync(name,
            LexiconScope.ForResolvedCampaign(requestedScope.CampaignId), cancellationToken);

        if (found.Value is not { } entry)
        {
            return new Error(ErrorCodes.Lexicon.NotFound, "Lexicon entity was not found.");
        }

        LexiconCurationScope scope = entry.ScopeCampaignId is { } campaign
            ? new(LexiconScopeKind.Campaign, campaign) : new(LexiconScopeKind.Global, null);

        LexiconEntryLifecycle lifecycle = new(entry.RetiredAtUtc, entry.PinnedAtUtc);

        LexiconCanonicalValue canonical = LexiconValueNormalizer.NormalizeCorrection(entry.Name, entry.Type, entry.Facts).Value;

        string digest = LexiconSnapshotDigest.ComputeHex(canonical);

        LexiconCurationTarget target = new(scope, canonical.NameNormalized, entry.Id, entry.CurationGeneration,
            digest, lifecycle, new(false, null, null, null, null, null, null), new(false, null, null, null, null));

        return new LexiconInspectionResult<LexiconEntryDetail>(new(entry, scope, null, lifecycle, entry.Eligibility,
            entry.CurationGeneration, digest, target, [], []), false);
    }

}
