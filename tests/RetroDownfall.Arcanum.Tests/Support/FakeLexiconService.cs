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

    private readonly Dictionary<string, LexiconEntryDto> _entries = new(StringComparer.OrdinalIgnoreCase);

    public Task<Result<LexiconEntryDto>> UpsertAsync(
        string name,
        string? type,
        IReadOnlyList<string> facts,
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

        if (_entries.TryGetValue(normalized, out LexiconEntryDto? existing))
        {
            List<string> merged = [.. existing.Facts, .. incoming];

            string resolvedType = string.IsNullOrWhiteSpace(type) ? existing.Type : type!;

            LexiconEntryDto updated = existing with { Type = resolvedType, Facts = merged.ToArray(), UpdatedAt = DateTimeOffset.UtcNow };

            _entries[normalized] = updated;

            return Task.FromResult(Result<LexiconEntryDto>.Success(updated));
        }

        LexiconEntryDto entry = new(
            Guid.NewGuid(),
            trimmedName,
            string.IsNullOrWhiteSpace(type) ? LexiconLimits.DefaultType : type!,
            incoming.ToArray(),
            DateTimeOffset.UtcNow);

        _entries[normalized] = entry;

        return Task.FromResult(Result<LexiconEntryDto>.Success(entry));
    }

    public Task<Result<bool>> DeleteByNameAsync(string name, CancellationToken cancellationToken = default)
    {

        string normalized = name.Trim().ToUpperInvariant();

        bool removed = _entries.Remove(normalized);

        return Task.FromResult(Result<bool>.Success(removed));
    }

    public Task<Result<IReadOnlyList<LexiconEntryDto>>> MatchEntitiesAsync(
        IReadOnlyList<string> entities,
        int limit,
        CancellationToken cancellationToken = default)
    {

        IReadOnlyList<LexiconEntryDto> results = _entries.Values.Take(limit).ToArray();

        return Task.FromResult(Result<IReadOnlyList<LexiconEntryDto>>.Success(results));
    }

    public Task<Result<LexiconEntryDto?>> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {

        string normalized = name.Trim().ToUpperInvariant();

        _entries.TryGetValue(normalized, out LexiconEntryDto? entry);

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
