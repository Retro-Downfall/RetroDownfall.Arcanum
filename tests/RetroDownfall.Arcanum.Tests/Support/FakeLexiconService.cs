using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// In-memory <see cref="ILexiconService"/> for MCP / pipeline tests that do not exercise the real
/// Grimoire. Mirrors the real service's name-normalization and append semantics closely enough for
/// tool-call assertions.
/// </summary>
public sealed class FakeLexiconService : ILexiconService
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

    public Task<Result<bool>> DeleteByNameAsync(
        string name,
        LexiconScope scope,
        CancellationToken cancellationToken = default)
    {

        string normalized = name.Trim().ToUpperInvariant();

        bool removed = _entries.Remove((scope.Key, normalized));

        return Task.FromResult(Result<bool>.Success(removed));
    }

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

    public Task<Result<IReadOnlyList<LexiconEntryDto>>> ListAsync(
        CancellationToken cancellationToken = default)
    {

        IReadOnlyList<LexiconEntryDto> entries = _entries.Values
            .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(
            Result<IReadOnlyList<LexiconEntryDto>>.Success(entries));

    }

}
