using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class ConclaveArchmage
{
    public bool SupportsWorkspaceMutations => CanIssueGuild() && quests!.HasWorkspaceMutationFence;

    private async Task<Result<GuildLocalChildToolResult>> MutateGuildWorkspaceAsync(GuildLocalChildToolCall call, CancellationToken ct)
    {
        GuildChildToolAdmission admission = call.Admission;

        if (admission.Registry.WorkspaceMutations is not { } runtime) return UnavailableGuild();

        var admitted = await quests!.AdmitWorkspaceMutationToolAsync(admission, ct).ConfigureAwait(false);

        if (admitted.IsFailure) return admitted.Error;

        var result = await GuildWorkspaceMutationTools.ExecuteAsync(admission, admitted.Value, runtime, settings.CurrentValue,
            token => quests.RevalidateWorkspaceMutationToolAsync(admission, token), ct).ConfigureAwait(false);

        if (result.IsFailure) return result.Error;

        using JsonDocument parsed = JsonDocument.Parse(result.Value.Text);

        JsonElement projection = parsed.RootElement.Clone();

        Result bounded = admission.ValidateProjectionAllocation(projection);

        if (bounded.IsFailure) return bounded.Error;

        admission.RecordCommittedProjection(call.ParentQuest, projection);

        return new GuildLocalChildToolResult(call.ParentQuest, Projection: projection, ChangedWorkspacePaths: result.Value.ChangedPaths);
    }
}
