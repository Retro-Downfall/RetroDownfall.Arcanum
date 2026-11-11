using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<GuildMessageReceipt>> PublishCohortTaskMessageAsync(GuildChildToolAdmission admission,
        Guid cohortId, Guid mutation, string mimeType, string body, CancellationToken ct) =>
        new GuildPortalStore(this, authority, policy, time).PublishAsync(admission.Registry.Parent.Driver.Caller!,
            admission.Registry.Parent.Driver.Execution.QuestId,
            new(mutation, new(GuildCircleAddressKind.Cohort, null, cohortId, null), GuildEnvelopeKind.Progress, mimeType, body, null), ct, admission);

    internal async Task<Result> AuthorizeCohortMessageToolWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildChildToolAdmission admission, CancellationToken ct)
    {
        if (!admission.Registry.Owns(admission) || !ReferenceEquals(admission.Registry.Parent.Store, this)
            || !ReferenceEquals(admission.Registry.CaptureInvocation(admission.Tool), admission.Frame)) return InvalidReceipt;

        var live = await RequireLocalToolWithinAsync(connection, transaction, signets, admission.Registry,
            admission.Tool, admission.Eligibility, ct).ConfigureAwait(false);

        return live.IsFailure ? live.Error : Result.Success();
    }
}
