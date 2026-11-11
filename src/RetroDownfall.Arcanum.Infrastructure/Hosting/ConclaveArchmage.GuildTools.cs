using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class ConclaveArchmage : IGuildLocalChildToolDispatcher
{
    public bool SupportsLocal => settings.CurrentValue.Features.Conclave && GuildLocalExecutionAdmission.IsAvailable(settings.CurrentValue);

    public bool SupportsCohorts => cohorts is not null && CanIssueGuild();

    public async Task<Result<GuildLocalChildToolResult>> DispatchAsync(GuildLocalChildToolCall call, CancellationToken cancellationToken)
    {
        if (call is null || call.Admission is null || !CanIssueGuild()
            || !ReferenceEquals(call.Admission.Registry.Parent.Store, quests)
            || !call.Admission.Registry.Owns(call.Admission) || call.Kind != call.Admission.Kind
            || call.ParentQuest.Id != call.Admission.Registry.Parent.Driver.Execution.QuestId)
            return UnavailableGuild();

        if (call.Kind == GuildLocalChildToolKind.SearchMemory)
            return await SearchGuildMemoryAsync(call, cancellationToken).ConfigureAwait(false);

        if (GuildLocalToolRegistry.IsCohortTool(call.Admission.Tool.Name))
            return await DispatchCohortToolAsync(call, cancellationToken).ConfigureAwait(false);

        if (call.Kind is not (GuildLocalChildToolKind.DelegateTask or GuildLocalChildToolKind.CreateAgentTask))
            return await DispatchTaskManagementAsync(call, cancellationToken).ConfigureAwait(false);

        if (call.Kind == GuildLocalChildToolKind.CreateAgentTask && GuildTaskArguments.IsRemote(call.Arguments))
            return await DispatchRemoteToolAsync(call, cancellationToken).ConfigureAwait(false);

        if (call.Kind == GuildLocalChildToolKind.CreateAgentTask && !SupportsLocal) return UnavailableGuild();

        Result<GuildIssueChildRequest> request = call.Kind == GuildLocalChildToolKind.CreateAgentTask
            ? GuildTaskArguments.Creation(call) : ChildToolRequest(call);

        if (request.IsFailure) return request.Error;

        GuildChildToolAdmission admission = call.Admission;

        GuildCallerContext caller = admission.Registry.Parent.Driver.Caller!;

        GuildIssuanceIdentity issuance = new(caller.InstallationGeneration, call.ParentQuest.Id, null,
            admission.IssuingStepId, admission.LogicalCallId);

        Result<GuildQuestStored> accepted = await quests!.IssueToolChildAsync(admission, issuance, request.Value, cancellationToken).ConfigureAwait(false);

        if (accepted.IsFailure) return accepted.Error;

        await using IAsyncDisposable? capacity = call.Kind == GuildLocalChildToolKind.DelegateTask
            ? await admission.Registry.Parent.SuspendForChildAsync(cancellationToken).ConfigureAwait(false) : null;

        guildScheduler!.Schedule(accepted.Value.Quest);

        if (call.Kind == GuildLocalChildToolKind.CreateAgentTask) return new GuildLocalChildToolResult(accepted.Value.Quest,
            Projection: JsonSerializer.SerializeToElement(accepted.Value.Quest, GuildJsonContext.Default.GuildQuestDto));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Result<GuildLocalResultObservation> observed = await quests.ReadLocalResultAsync(caller, accepted.Value.Quest.Id,
                true, cancellationToken).ConfigureAwait(false);

            if (observed.IsFailure) return observed.Error;

            if (observed.Value.Receipt is { } receipt)
            {
                Result<GuildQuestStored> parent = await quests.GetAsync(caller, call.ParentQuest.Id, cancellationToken).ConfigureAwait(false);

                if (parent.IsFailure) return parent.Error;

                Result<GuildResultApplicationReceipt> applied = await quests.ApplyResultAsync(caller,
                    new(new(receipt.ResultId, receipt.CanonicalContentDigest), parent.Value.Quest.Id, receipt.QuestId,
                        receipt.ResultId, receipt.CanonicalContentDigest, parent.Value.Quest.Revision), cancellationToken).ConfigureAwait(false);

                if (applied.IsFailure)
                {
                    if (applied.Error.Code == ErrorCodes.Guild.RevisionConflict) continue;

                    return applied.Error;
                }

                string summary = observed.Value.Body ?? $"Child Quest {receipt.Outcome}: {receipt.ReasonCode}";

                return new GuildLocalChildToolResult(observed.Value.Quest, summary);
            }

            if (observed.Value.Quest.ReconciliationRequired)
                return new Error(ErrorCodes.Guild.ReconciliationRequired, "The existing child Quest requires reconciliation; its work will not be repeated.");

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private static Result<GuildIssueChildRequest> ChildToolRequest(GuildLocalChildToolCall call)
    {
        // Only the registry's frozen argument object enters the logical issuance intent.
        JsonElement arguments = call.Admission.Arguments;

        bool sterile = call.Kind == GuildLocalChildToolKind.DelegateTask;

        string key = sterile ? "prompt" : "goal";

        if (arguments.ValueKind != JsonValueKind.Object
            || arguments.EnumerateObject().Any(property => sterile
                ? property.Name is not ("prompt" or "files" or "max_tokens" or "max_cost_usd")
                : property.Name is not ("goal" or "name"))
            || !arguments.TryGetProperty(key, out JsonElement goal) || goal.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(goal.GetString())) return InvalidChildTool;

        string? name = null;

        if (!sterile && arguments.TryGetProperty("name", out JsonElement suppliedName))
        {
            if (suppliedName.ValueKind != JsonValueKind.String) return InvalidChildTool;

            name = suppliedName.GetString();
        }

        GuildBudgetGrant budget = call.ParentQuest.ResolvedGrant.Budget;

        if (sterile)
        {
            bool tokens = arguments.TryGetProperty("max_tokens", out JsonElement tokenLimit);

            bool usd = arguments.TryGetProperty("max_cost_usd", out JsonElement usdLimit);

            if (!tokens && !usd) return InvalidChildTool;

            if (tokens)
            {
                if (tokenLimit.ValueKind != JsonValueKind.Number || !tokenLimit.TryGetInt64(out long value) || value <= 0) return InvalidChildTool;

                budget = budget with { Tokens = new(GuildBudgetMode.Finite, value) };
            }

            if (usd)
            {
                if (usdLimit.ValueKind != JsonValueKind.Number || !ExactDecimalText.TryParse(usdLimit.GetRawText(), out decimal value)
                    || value <= 0) return InvalidChildTool;

                budget = budget with { Usd = new(GuildBudgetMode.Finite, value) };
            }
        }

        StringBuilder prompt = new(goal.GetString());

        if (sterile && arguments.TryGetProperty("files", out JsonElement files))
        {
            if (files.ValueKind != JsonValueKind.Array) return InvalidChildTool;

            foreach (JsonElement file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object
                    || file.EnumerateObject().Any(property => property.Name is not ("path" or "content"))
                    || !file.TryGetProperty("path", out JsonElement path) || path.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(path.GetString()) || path.GetString()!.Length > 4096
                    || !file.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.String)
                    return InvalidChildTool;

                prompt.AppendLine();

                // A file name is untrusted too. Neither the name nor the bytes are a path to open.
                SystemPromptBuilder.AppendUntrusted(prompt, "Explicit selected file", path.GetString() + "\n" + content.GetString());
            }
        }

        if (Encoding.UTF8.GetByteCount(prompt.ToString()) > GuildStore.MaximumContentBytes) return InvalidChildTool;

        GuildRequestedGrant requested = sterile
            ? new(Workspaces: new(true, []), Tools: new(true, []), Memory: new(true, []), Budget: new(true, budget), Delegation: new(true, []))
            : new(Budget: new(true, budget));

        return new GuildIssueChildRequest(call.Admission.LogicalCallId, call.ParentQuest.Revision, null, name,
            prompt.ToString(), requested);
    }

    private static Error InvalidChildTool => new(ErrorCodes.Guild.InvalidRequest,
        "The local child request must contain supported explicit input and a valid budget; protected source selection requires a qualified adapter.");
}
