using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Api.Conclave;

internal static partial class GuildEndpoints
{
    private static RouteGroupBuilder MapGuildOperatorEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/guild/quests/{questId:guid}/tree", async (Guid questId, string? cursor, int? pageSize,
            GuildNativeHost host, GuildQuestStore store, HttpContext context) =>
        {
            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<GuildQuestPageDto> result = caller.IsFailure ? caller.Error
                : await store.GetOperatorTreePageAsync(caller.Value, questId, new(cursor, pageSize ?? 100), context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildQuestPageDto);
        }).WithName("GuildGetQuestTree");

        api.MapGet("/guild/quests/{questId:guid}/spend", async (Guid questId,
            GuildNativeHost host, GuildQuestStore store, HttpContext context) =>
        {
            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<GuildSpendDto> result = caller.IsFailure ? caller.Error
                : await ((IGuildTreeSpendLedger)store).ReadAsync(caller.Value, questId, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildSpendDto);
        }).WithName("GuildGetTreeSpend");

        api.MapPost("/guild/quests/{questId:guid}/children", async (Guid questId, GuildIssueChildRequest? request,
            GuildNativeHost host, IGuildQuestCoordinator coordinator, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty)
                return Reply(Result<GuildQuestDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

            Result<string> intent = GuildQuestIntent.Compute(questId, request);

            if (intent.IsFailure)
                return Reply(Result<GuildQuestDto>.Failure(intent.Error), context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

            Result ready = host.RequireExecutionReady();

            if (ready.IsFailure)
                return Reply(Result<GuildQuestDto>.Failure(ready.Error), context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);

            Result<GuildQuestDto> result = caller.IsFailure ? caller.Error
                : await coordinator.IssueAsync(caller.Value, new(request.IdempotencyKey, intent.Value), questId, request, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto,
                result.IsSuccess && result.Value.Receipt?.Replayed != true ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        }).WithName("GuildIssueChild");

        api.MapGet("/guild/cohorts/{cohortId:guid}/members", async (Guid cohortId, string? cursor, int? pageSize,
            GuildNativeHost host, GuildCohortStore cohorts, HttpContext context) =>
        {
            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<CohortMemberPageDto> result = caller.IsFailure ? caller.Error
                : await cohorts.GetOperatorMemberPageAsync(caller.Value, cohortId, new(cursor, pageSize ?? 100), context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseCohortMemberPageDto);
        }).WithName("GuildListCohortMembers");

        api.MapGet("/guild/quests", async (Guid? rootId, Guid? parentId, string? state, string? cursor, int? pageSize,
            GuildNativeHost host, GuildQuestStore store, HttpContext context) =>
        {
            QuestState? filter = state switch
            {
                null => null,
                "submitted" => QuestState.Submitted,
                "working" => QuestState.Working,
                "input-required" => QuestState.InputRequired,
                "completed" => QuestState.Completed,
                "failed" => QuestState.Failed,
                "canceled" => QuestState.Canceled,
                "rejected" => QuestState.Rejected,
                _ => (QuestState)(-1),
            };

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<GuildQuestPageDto> result = caller.IsFailure ? caller.Error
                : await store.ListOperatorAsync(caller.Value, new(rootId, parentId, filter, cursor, pageSize ?? 100), context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildQuestPageDto);
        }).WithName("GuildListQuests");

        api.MapPost("/guild/signets/{signetId:guid}/revoke", async (Guid signetId, GuildRevokeSignetRequest? request,
            GuildNativeHost host, ISignetAuthority authority, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty || request.ExpectedRevision < 1
                || request.ReasonCode != GuildCancellationReason.OperatorRequested)
                return Reply(Result<SignetRevocationDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseSignetRevocationDto);

            byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.GuildRevokeSignetRequest);

            byte[] prefix = Encoding.ASCII.GetBytes("guild-revoke-v1:" + signetId.ToString("D").ToUpperInvariant() + ":");

            string digest = Convert.ToHexString(SHA256.HashData([.. prefix, .. body]));

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);

            Result<SignetRevocationDto> result = caller.IsFailure ? caller.Error
                : await authority.RevokeAsync(caller.Value, new(request.IdempotencyKey, digest), signetId, request, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseSignetRevocationDto,
                result.IsSuccess && result.Value.Receipt?.Replayed == true ? StatusCodes.Status200OK
                    : result.IsSuccess && result.Value.CancellationRequested && !result.Value.CancellationConverged ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }).WithName("GuildRevokeSignet");

        api.MapGet("/guild/cohorts/{cohortId:guid}", async (Guid cohortId, GuildNativeHost host,
            GuildCohortStore cohorts, HttpContext context) =>
        {
            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<CohortDto> result = caller.IsFailure ? caller.Error
                : await cohorts.GetOperatorCohortAsync(caller.Value, cohortId, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseCohortDto);
        }).WithName("GuildGetCohort");

        api.MapPost("/guild/cohorts", async (CreateCohortRequest? request, GuildNativeHost host,
            GuildCohortStore cohorts, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty)
                return Reply(Result<CohortDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseCohortDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);

            Result<CohortDto> result = caller.IsFailure ? caller.Error
                : await cohorts.CreateOperatorAsync(caller.Value, request, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseCohortDto,
                result.IsSuccess && result.Value.Receipt?.Replayed != true ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        }).WithName("GuildCreateCohort");

        api.MapGet("/guild/cohorts", async (Guid? rootId, string? state, string? cursor, int? pageSize,
            GuildNativeHost host, GuildCohortStore cohorts, HttpContext context) =>
        {
            GuildCohortState? filter = state switch
            {
                null => null,
                "open" => GuildCohortState.Open,
                "closed" => GuildCohortState.Closed,
                "draining" => GuildCohortState.Draining,
                "completed" => GuildCohortState.Completed,
                "failed" => GuildCohortState.Failed,
                "canceled" => GuildCohortState.Canceled,
                _ => (GuildCohortState)(-1),
            };

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<CohortPageDto> result = caller.IsFailure ? caller.Error
                : await cohorts.ListOperatorAsync(caller.Value, rootId, filter, new(cursor, pageSize ?? 100), context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseCohortPageDto);
        }).WithName("GuildListCohorts");

        api.MapGet("/guild/cohorts/{cohortId:guid}/members/{memberId:guid}/quests", async (Guid cohortId, Guid memberId,
            string? cursor, int? pageSize, GuildNativeHost host, GuildCohortStore cohorts, HttpContext context) =>
        {
            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<CohortMemberQuestPageDto> result = caller.IsFailure ? caller.Error
                : await cohorts.GetOperatorMemberQuestsAsync(caller.Value, cohortId, memberId, new(cursor, pageSize ?? 100), context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseCohortMemberQuestPageDto);
        }).WithName("GuildListCohortMemberQuests");

        api.MapPost("/guild/cohorts/{cohortId:guid}/close", async (Guid cohortId, GuildRevisionRequest? request,
            GuildNativeHost host, GuildCohortStore cohorts, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty)
                return Reply(Result<CohortDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseCohortDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);

            Result<CohortDto> result = caller.IsFailure ? caller.Error
                : await cohorts.CloseOperatorAsync(caller.Value, cohortId, request, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseCohortDto);
        }).WithName("GuildCloseCohort");

        api.MapPost("/guild/cohorts/{cohortId:guid}/members", async (Guid cohortId, AdmitCohortMemberRequest? request,
            GuildNativeHost host, GuildCohortStore cohorts, GuildQuestStore quests, IGuildLocalQuestScheduler scheduler, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty)
                return Reply(Result<CohortMemberDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseCohortMemberDto);

            Result ready = host.RequireExecutionReady();

            if (ready.IsFailure) return Reply(Result<CohortMemberDto>.Failure(ready.Error), context, ArcanumJsonContext.Default.ApiResponseCohortMemberDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);

            Result<CohortDto> cohort = caller.IsFailure ? caller.Error : await cohorts.GetOperatorCohortAsync(caller.Value, cohortId, context.RequestAborted).ConfigureAwait(false);

            Result<GuildOperatorMemberAdmission> admission = cohort.IsFailure ? cohort.Error
                : await quests.IssueOperatorCohortMemberAsync(caller.Value, cohort.Value, request, context.RequestAborted).ConfigureAwait(false);

            if (admission.IsSuccess) scheduler.Schedule(admission.Value.Quest);

            Result<CohortMemberDto> result = admission.IsFailure ? admission.Error : admission.Value.Member;

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseCohortMemberDto,
                result.IsSuccess && result.Value.Receipt?.Replayed != true ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        }).WithName("GuildAdmitCohortMember");

        api.MapPost("/guild/cohorts/{cohortId:guid}/members/{memberId:guid}/remove", async (Guid cohortId, Guid memberId,
            GuildRevisionRequest? request, GuildNativeHost host, GuildCohortStore cohorts, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty)
                return Reply(Result<CohortMemberDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseCohortMemberDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);

            Result<CohortMemberDto> result = caller.IsFailure ? caller.Error
                : await cohorts.RemoveOperatorAsync(caller.Value, cohortId, memberId, request, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseCohortMemberDto,
                result.IsSuccess && result.Value.Receipt?.Replayed == true ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
        }).WithName("GuildRemoveCohortMember");

        api.MapPost("/guild/cohorts/{cohortId:guid}/cancel", async (Guid cohortId, GuildRevisionRequest? request,
            GuildNativeHost host, GuildCohortStore cohorts, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty)
                return Reply(Result<CohortDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseCohortDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);

            Result<CohortDto> result = caller.IsFailure ? caller.Error
                : await cohorts.CancelOperatorAsync(caller.Value, cohortId, request, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseCohortDto,
                result.IsSuccess && result.Value.Receipt?.Replayed == true ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
        }).WithName("GuildCancelCohort");

        api.MapGet("/guild/cohorts/{cohortId:guid}/contributions", async (Guid cohortId, bool? selected, int? pageSize,
            string? cursor, GuildNativeHost host, GuildCohortStore cohorts, HttpContext context) =>
        {
            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<CohortContributionPageDto> result = caller.IsFailure ? caller.Error
                : await cohorts.ListOperatorContributionsAsync(caller.Value, cohortId, selected,
                    new GuildPageQuery(cursor, pageSize ?? 100), context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseCohortContributionPageDto);
        }).WithName("GuildListCohortContributions");

        api.MapGet("/guild/cohorts/{cohortId:guid}/convergence", async (Guid cohortId, GuildNativeHost host,
            GuildCohortStore cohorts, HttpContext context) =>
        {
            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<CohortConvergenceDto> result = caller.IsFailure ? caller.Error
                : await cohorts.GetOperatorConvergenceAsync(caller.Value, cohortId, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseCohortConvergenceDto);
        }).WithName("GuildGetCohortConvergence");

        api.MapGuildMarketEndpoints();

        return api;
    }
}
