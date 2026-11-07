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

    /// <summary>Compatibility guidance of unknown origin; never an authenticated operator action.</summary>
    Task<Result<string>> InterveneAsync(
        Guid apprenticeId,
        string guidance,
        bool resume,
        CancellationToken cancellationToken = default);

    /// <summary>Native authenticated operator guidance; the request body cannot select its provenance.</summary>
    Task<Result<string>> InterveneOperatorAsync(
        Guid apprenticeId,
        string guidance,
        bool resume,
        ApprenticeOperatorInterventionContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Legacy remote continuation. No verified peer principal means unknown provenance.</summary>
    Task<Result<string>> ContinueAsync(
        Guid apprenticeId,
        string guidance,
        bool resume,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ApprenticeEvent> SubscribeChronicleAsync(
        Guid apprenticeId,
        CancellationToken cancellationToken = default);

}
