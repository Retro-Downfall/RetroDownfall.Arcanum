using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Conclave;

public interface IApprenticeRuntime
{

    Task<Result<string>> StartAsync(Guid apprenticeId, CancellationToken cancellationToken = default);

    Task<Result<string>> PauseAsync(Guid apprenticeId, CancellationToken cancellationToken = default);

    Task<Result<string>> ResumeAsync(Guid apprenticeId, CancellationToken cancellationToken = default);

    Task<Result<string>> CancelAsync(Guid apprenticeId, CancellationToken cancellationToken = default);

    Task<Result<ApprenticeDetailDto>> ReweaveAsync(
        Guid apprenticeId,
        IReadOnlyList<PlanStep> steps,
        CancellationToken cancellationToken = default);

    Task<Result<string>> InterveneAsync(
        Guid apprenticeId,
        string guidance,
        bool resume,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ApprenticeEvent> SubscribeChronicleAsync(
        Guid apprenticeId,
        CancellationToken cancellationToken = default);

}
