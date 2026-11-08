using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Explicit production refusal until an authenticated coordinator policy is installed.</summary>
internal sealed class UnavailableGuildSignetHostPolicy : IGuildSignetHostPolicy
{
    public Guid InstallationGeneration => Guid.Empty;

    public Task<Result<GuildSignetAdmissionEvidence>> AuthorizeAdmissionAsync(GuildAdmissionContext admission,
        GuildSignetBinding binding, CancellationToken cancellationToken) =>
        Task.FromResult(Result<GuildSignetAdmissionEvidence>.Failure(Unavailable));

    public Task<Result<GuildSignetPolicyEvidence>> ResolveCurrentAsync(ImmutableArray<GuildSignetRecord> lineage,
        CancellationToken cancellationToken) => Task.FromResult(Result<GuildSignetPolicyEvidence>.Failure(Unavailable));

    public Task<Result> AuthorizeRevocationAsync(GuildCallerContext caller, GuildSignetRecord target,
        CancellationToken cancellationToken) => Task.FromResult(Result.Failure(Unavailable));

    public Task<Result> AuthorizeRecoveryAsync(GuildRecoveryContext recovery, GuildSignetRecord target,
        CancellationToken cancellationToken) => Task.FromResult(Result.Failure(Unavailable));

    private static Error Unavailable => new(ErrorCodes.Guild.AuthorityUnavailable,
        "The host has no authenticated Guild admission policy.");
}
