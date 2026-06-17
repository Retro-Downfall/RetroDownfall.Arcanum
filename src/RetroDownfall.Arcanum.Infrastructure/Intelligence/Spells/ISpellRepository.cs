using RetroDownfall.Arcanum.Core.TheForge;
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

    Task<Result<SpellSummary>> ImportAsync(SpellImportRequest request, CancellationToken ct);

}
