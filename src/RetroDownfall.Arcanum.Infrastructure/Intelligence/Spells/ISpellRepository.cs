using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

public interface ISpellRepository
{
    Task<SpellSummary[]> ListAsync(string? workingDirectory, CancellationToken ct);

    Task<SpellDetail?> GetAsync(string name, string? workingDirectory, CancellationToken ct);

    Task<Result> CreateAsync(string? workingDirectory, CreateSpellRequest request, CancellationToken ct);

    Task<Result> UpdateAsync(string name, string? workingDirectory, UpdateSpellRequest request, CancellationToken ct);

    Task<Result> DeleteAsync(string name, string? workingDirectory, CancellationToken ct);

    Task<SpellSummary[]> SearchAsync(SpellSearchQuery query, CancellationToken ct);

    Task<SpellValidationResultDto> ValidateAsync(string name, string? workingDirectory, CancellationToken ct);

    Task<SpellExportDto?> ExportAsync(string name, string? workingDirectory, CancellationToken ct);

    async Task<IReadOnlyList<(string Name, SpellExportDto? Export)>> ExportAllAsync(string workingDirectory, CancellationToken ct)
    {
        var exports = new List<(string Name, SpellExportDto? Export)>();

        foreach (SpellSummary summary in await ListAsync(workingDirectory, ct).ConfigureAwait(false))
        {
            if (summary.Source != SpellSource.Builtin)
            {
                exports.Add((summary.Name, await ExportAsync(summary.Name, workingDirectory, ct).ConfigureAwait(false)));
            }
        }

        return exports;
    }

    async Task<IReadOnlyList<Result<SpellSummary>>> ImportBatchAsync(
        string workingDirectory, IReadOnlyList<SpellImportRequest> requests, CancellationToken ct)
    {
        var results = new List<Result<SpellSummary>>(requests.Count);

        foreach (SpellImportRequest request in requests)
        {
            Result<SpellSummary> result = await ImportAsync(request with { Workspace = workingDirectory }, ct).ConfigureAwait(false);

            results.Add(result);

            if (result.IsSuccess)
            {
                ct = CancellationToken.None;
            }
        }

        return results;
    }

    Task<Result<SpellSummary>> ImportAsync(SpellImportRequest request, CancellationToken ct);

    Task<Result<SpellSummary>> CloneAsync(string name, string? workingDirectory, CloneSpellRequest request, CancellationToken ct);

    Task<Result<SpellVersionDto>> CreateVersionAsync(string name, string? workingDirectory, CreateSpellVersionRequest request, CancellationToken ct);

    Task<Result<SpellVersionDto>> UpdateVersionAsync(string name, string version, string? workingDirectory, UpdateSpellVersionRequest request, CancellationToken ct);

    Task<Result<SpellVersionDto>> ActivateVersionAsync(string name, string version, string? workingDirectory, CancellationToken ct);

    Task<Result<SpellVersionDetailDto>> GetVersionDetailAsync(string name, string version, string? workingDirectory, CancellationToken ct);
}
