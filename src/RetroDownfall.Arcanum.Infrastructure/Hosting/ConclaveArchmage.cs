using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Default <see cref="IConclaveArchmage"/>: creates child Apprentices for The Conclave's
/// cross-Apprentice delegation (Cast Sending). Persists lineage in the child's checkpoint JSON so
/// no schema change is required.
/// </summary>
internal sealed class ConclaveArchmage(
    IApprenticeRepository repository,
    IOptionsMonitor<ArcanumSettings> settings) : IConclaveArchmage
{

    public async Task<Result<Apprentice>> CastAsync(
        ConclaveCastRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!settings.CurrentValue.Conclave.Enabled)
        {
            return Result<Apprentice>.Failure(
                new Error("Apprentice.ConclaveDisabled", "The Conclave is disabled; cross-Apprentice delegation is not available."));
        }

        if (string.IsNullOrWhiteSpace(request.Goal))
        {
            return Result<Apprentice>.Failure(
                new Error("Apprentice.InvalidGoal", "A non-empty goal is required to cast a sending."));
        }

        if (string.IsNullOrWhiteSpace(request.WorkspacePath))
        {
            return Result<Apprentice>.Failure(
                new Error("Apprentice.InvalidWorkspace", "A workspace is required to cast a sending."));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        string name = string.IsNullOrWhiteSpace(request.Name)
            ? $"Sending {Guid.NewGuid().ToString("N")[..8]}"
            : request.Name.Trim();

        Apprentice child = new()
        {
            Id = Guid.NewGuid(),
            CampaignId = request.CampaignId,
            Name = name,
            Goal = request.Goal.Trim(),
            Plan = "[]",
            CurrentStep = 0,
            Status = ApprenticeStatus.Idle.ToString(),
            WorkspacePath = request.WorkspacePath,
            ParentApprenticeId = request.ParentApprenticeId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (request.ParentApprenticeId is { } parentId)
        {
            child.CheckpointData = ApprenticeRepository.SerializeCheckpoint(new ApprenticeCheckpoint
            {
                CurrentStep = 0,
                Timestamp = now,
                ParentApprenticeId = parentId,
            });
        }

        await repository.AddAsync(child, cancellationToken).ConfigureAwait(false);

        return Result<Apprentice>.Success(child);
    }

}
