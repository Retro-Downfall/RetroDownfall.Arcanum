using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Memory;

/// <summary>Prepares and applies the selective hard erasure of one Saga memory and its twins.</summary>
/// <remarks>
/// Prepare and apply only. Release and administration are separate ports, so no store implementation
/// can grow a second path that re-enables what it erased.
/// </remarks>
public interface ISagaMemoryErasureService
{
    Task<Result<MemoryErasurePreflightDto>> PrepareAsync(
        SagaErasePrepareRequest request,
        CancellationToken cancellationToken);

    Task<Result<MemoryErasureResultDto>> ApplyAsync(
        SagaEraseRequest request,
        OperatorAuthorityContext authority,
        CancellationToken cancellationToken);
}

/// <summary>Prepares and applies the selective hard erasure of one exact Lexicon entry.</summary>
/// <remarks>Prepare and apply only, for the same reason as <see cref="ISagaMemoryErasureService"/>.</remarks>
public interface ILexiconErasureService
{
    Task<Result<MemoryErasurePreflightDto>> PrepareAsync(
        LexiconErasePrepareRequest request,
        CancellationToken cancellationToken);

    Task<Result<MemoryErasureResultDto>> ApplyAsync(
        LexiconEraseRequest request,
        OperatorAuthorityContext authority,
        CancellationToken cancellationToken);
}

/// <summary>Prepares and applies the selective hard erasure of one Covenant entry.</summary>
/// <remarks>
/// Prepare and apply only, for the same reason as <see cref="ISagaMemoryErasureService"/>. Prepare
/// takes the operator's authority too, because the Covenant preflight token binds the authority epoch.
/// </remarks>
public interface ICovenantEntryErasureService
{
    Task<Result<MemoryErasurePreflightDto>> PrepareAsync(
        CovenantErasePrepareRequest request,
        OperatorAuthorityContext authority,
        CancellationToken cancellationToken);

    Task<Result<MemoryErasureResultDto>> ApplyAsync(
        CovenantEraseRequest request,
        OperatorAuthorityContext authority,
        CancellationToken cancellationToken);
}
