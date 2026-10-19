using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
/// <summary>Immutable, bounded content-free evidence only. No transient execution objects enter this sink.</summary>
public interface IMemoryEvaluationEvidenceStore
{
    Task<Result<MemoryEvaluationStoredEvidence>> RegisterAsync(MemoryEvaluationRegistration registration, ImmutableArray<MemoryEvaluationSourceCoordinate> sources, CancellationToken ct);
    Task<Result<MemoryEvaluationOwnership>> ReadOwnershipAsync(Guid evaluationId, CancellationToken ct);
    Task<Result<MemoryEvaluationStoredEvidence>> GetAsync(Guid evaluationId, CancellationToken ct);
    Task<Result<MemoryEvaluationStoredEvidence>> CompleteAsync(Guid evaluationId, MemoryEvaluationTerminalReport report, CancellationToken ct);
    Task<Result<MemoryEvaluationDeleteResult>> DeleteAsync(MemoryEvaluationDeleteRequest request, CancellationToken ct);
}
