using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class ConclaveArchmage
{
    private static readonly byte[] WorkspaceCursorKey = RandomNumberGenerator.GetBytes(32);

    public bool SupportsWorkspaceReads => CanIssueGuild();

    private async Task<Result<GuildLocalChildToolResult>> ReadGuildWorkspaceAsync(GuildLocalChildToolCall call, CancellationToken ct)
    {
        GuildChildToolAdmission admission = call.Admission;

        var admitted = await quests!.AdmitWorkspaceReadToolAsync(admission, ct).ConfigureAwait(false);

        if (admitted.IsFailure) return admitted.Error;

        GuildWorkspaceReadAdmission read = admitted.Value;

        GuildExecutionHandle execution = admission.Registry.Parent.Driver.Execution;

        byte[] cursorKey = HMACSHA256.HashData(WorkspaceCursorKey, Encoding.UTF8.GetBytes(
            $"Guild.WorkspaceRead.v1:{execution.RootQuestId:N}:{execution.QuestId:N}:{execution.RunId:N}:{execution.SignetId:N}:{read.WorkspaceId:N}"));

        ArcanumSettings current = settings.CurrentValue;

        long allocation = Math.Min(admission.ResponseAllocationBytes,
            ArcanumSettingClamps.ToolOutputCapBytes(current.ResolveIntelligence().ToolOutputCapBytes));

        Func<CancellationToken, Task<Result>>? verifyPhysical = null;

        Result<string> output = await GuildWorkspaceReadTools.ExecuteAsync(admission.Tool.Name, admission.Arguments,
            read.Physical, read.Grant, read.WorkspaceId, allocation, current.ResolveCodingTools().Search, cursorKey,
            token => quests.RevalidateWorkspaceReadToolAsync(admission, read, token), ct,
            verify => verifyPhysical = verify).ConfigureAwait(false);

        if (output.IsFailure) return output.Error;

        if (verifyPhysical is null) return UnavailableGuild();

        using JsonDocument result = JsonDocument.Parse(output.Value);

        JsonElement projection = result.RootElement.Clone();

        Result bounded = admission.ValidateProjectionAllocation(projection);

        if (bounded.IsFailure) return bounded.Error;

        admission.RecordCommittedProjection(call.ParentQuest, projection);

        return new GuildLocalChildToolResult(call.ParentQuest, Projection: projection,
            WorkspaceDisclosure: new(admission.Registry, admission.Tool, admission.Eligibility,
                read.WorkspaceId, read.Grant.Workspaces, admission.LogicalCallId, WorkspaceReadPaths(admission, projection), verifyPhysical));
    }

    private static ImmutableArray<string> WorkspaceReadPaths(GuildChildToolAdmission admission, JsonElement result) => admission.Tool.Name switch
    {
        "read_file" => [admission.Arguments.GetProperty("relativePath").GetString()!],
        "list_directory" => [.. result.GetProperty("paths").EnumerateArray().Select(path => path.GetString()!)],
        _ => [.. result.GetProperty("matches").EnumerateArray().Select(match => match.GetProperty("path").GetString()!).Distinct(StringComparer.Ordinal)],
    };
}
