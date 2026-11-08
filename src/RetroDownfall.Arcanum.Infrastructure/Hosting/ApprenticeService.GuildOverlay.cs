using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>Ephemeral unit state; never writes content-bearing fields into the canonical skeleton.</summary>
internal sealed class GuildApprenticeOverlay(Apprentice initial) : IApprenticeRepository
{
    private readonly Lock _gate = new();

    private Apprentice _value = Clone(initial);

    public Task<Result<bool>> IsGuildBoundAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<bool>.Success(id == initial.Id));

    public Task<Apprentice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate) return Task.FromResult(id == _value.Id ? Clone(_value) : null);
    }

    public Task<ListPageResult<Apprentice>> ListAsync(Guid? campaignId, string? status, int? limit = null,
        DateTimeOffset? beforeUpdatedAt = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("A Guild unit cannot enumerate legacy Apprentices.");

    public Task<Apprentice> AddAsync(Apprentice apprentice, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The Guild child already exists.");

    public Task<Apprentice> UpdateAsync(Apprentice apprentice, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            RequireIdentity(apprentice);

            _value = Clone(apprentice);

            return Task.FromResult(Clone(_value));
        }
    }

    public Task<bool> UpdateProgressAsync(Apprentice apprentice, string expectedPlan, int expectedCurrentStep, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            RequireIdentity(apprentice);

            if (_value.Plan != expectedPlan || _value.CurrentStep != expectedCurrentStep) return Task.FromResult(false);

            _value.Plan = apprentice.Plan;

            _value.CurrentStep = apprentice.CurrentStep;

            _value.CheckpointData = apprentice.CheckpointData;

            return Task.FromResult(true);
        }
    }

    public Task<bool> BindSessionAsync(Guid id, Guid sessionId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The Guild Session is already canonical.");

    public Task<bool> TryUpdateAsync(Apprentice apprentice, IReadOnlyCollection<string> expectedStatuses,
        int expectedCurrentStep, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            RequireIdentity(apprentice);

            if (_value.CurrentStep != expectedCurrentStep || !expectedStatuses.Contains(_value.Status)) return Task.FromResult(false);

            _value = Clone(apprentice);

            return Task.FromResult(true);
        }
    }

    public Task<bool> TryUpdateStatusAsync(Guid id, string status, IReadOnlyCollection<string> expectedStatuses,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (id != _value.Id || !expectedStatuses.Contains(_value.Status)) return Task.FromResult(false);

            _value.Status = status;

            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new InvalidOperationException("A Guild unit cannot delete its canonical child.");

    public Task<IReadOnlyList<Apprentice>> GetResumableAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Guild recovery belongs to the Quest.");

    private void RequireIdentity(Apprentice value)
    {
        if (value.Id != initial.Id || value.SessionId != initial.SessionId || value.CampaignId != initial.CampaignId
            || value.ParentApprenticeId != initial.ParentApprenticeId) throw new InvalidOperationException("The canonical Guild identity cannot change.");
    }

    private static Apprentice Clone(Apprentice value) => new()
    {
        Id = value.Id, CampaignId = value.CampaignId, Name = value.Name, Goal = value.Goal, Plan = value.Plan,
        CurrentStep = value.CurrentStep, Status = value.Status, SessionId = value.SessionId, WorkspacePath = value.WorkspacePath,
        CheckpointData = value.CheckpointData, ErrorMessage = value.ErrorMessage, CreatedAt = value.CreatedAt,
        UpdatedAt = value.UpdatedAt, ParentApprenticeId = value.ParentApprenticeId,
    };
}
