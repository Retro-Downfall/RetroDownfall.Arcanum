using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal static class GuildCohortToolArguments
{
    internal static Result<CreateCohortRequest> Create(GuildChildToolAdmission admission)
    {
        JsonElement arguments = admission.Arguments;

        if (admission.Kind != GuildLocalChildToolKind.CreateCohort
            || !GuildTaskArguments.Closed(arguments, "name", "policy", "quorum", "resultType", "grant")
            || !GuildTaskArguments.Text(arguments, "name", out string? name) || string.IsNullOrWhiteSpace(name)
            || !GuildTaskArguments.Text(arguments, "policy", out string? policy)
            || !GuildTaskArguments.Text(arguments, "resultType", out string? mime) || mime is not ("text/plain" or "application/json"))
            return GuildTaskArguments.Invalid;

        GuildConvergenceKind kind = policy switch
        {
            "all" => GuildConvergenceKind.All,
            "first" => GuildConvergenceKind.First,
            "quorum" => GuildConvergenceKind.Quorum,
            _ => 0,
        };

        int? quorum = null;

        if (kind == GuildConvergenceKind.Quorum)
        {
            if (!arguments.TryGetProperty("quorum", out JsonElement number) || number.ValueKind != JsonValueKind.Number
                || !number.TryGetInt32(out int value) || value < 1)
                return GuildTaskArguments.Invalid;

            quorum = value;
        }
        else if (kind == 0 || arguments.TryGetProperty("quorum", out _)) return GuildTaskArguments.Invalid;

        try
        {
            GuildRequestedGrant? grant = arguments.TryGetProperty("grant", out JsonElement requested)
                ? requested.Deserialize(GuildJsonContext.Default.GuildRequestedGrant) : null;

            if (arguments.TryGetProperty("grant", out _) && grant is null) return GuildTaskArguments.Invalid;

            return new CreateCohortRequest(admission.LogicalCallId, admission.Registry.Parent.Driver.Execution.QuestId,
                name.Trim(), new(kind, quorum), new(mime), grant);
        }
        catch (JsonException)
        {
            return GuildTaskArguments.Invalid;
        }
    }

    internal static bool MatchesCreate(GuildChildToolAdmission admission, CreateCohortRequest request)
    {
        var expected = Create(admission);

        return expected.IsSuccess && request is not null && JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(expected.Value, GuildJsonContext.Default.CreateCohortRequest),
            JsonSerializer.SerializeToElement(request, GuildJsonContext.Default.CreateCohortRequest));
    }

    internal static bool MatchesRevision(GuildChildToolAdmission admission, Guid cohortId, Guid? memberId, GuildRevisionRequest request)
    {
        bool remove = memberId is not null;

        JsonElement arguments = admission.Arguments;

        return admission.Kind == (remove ? GuildLocalChildToolKind.RemoveCohortMember : GuildLocalChildToolKind.CloseCohort)
            && GuildTaskArguments.Closed(arguments, remove ? ["cohortId", "memberId", "expectedRevision"] : ["cohortId", "expectedRevision"])
            && GuildTaskArguments.Identity(arguments, "cohortId", out Guid target) && target == cohortId
            && (!remove || GuildTaskArguments.Identity(arguments, "memberId", out Guid member) && member == memberId)
            && Revision(arguments, out long revision) && revision == request.ExpectedRevision
            && request.IdempotencyKey == admission.LogicalCallId;
    }

    internal static bool MatchesAdmission(GuildChildToolAdmission admission, Guid cohortId, long expectedCohortRevision,
        GuildIssueChildRequest request)
    {
        if (admission.Kind != GuildLocalChildToolKind.AdmitCohortMember
            || !GuildTaskArguments.Identity(admission.Arguments, "cohortId", out Guid target) || target != cohortId
            || !Revision(admission.Arguments, out long revision) || revision != expectedCohortRevision
            || request.IdempotencyKey != admission.LogicalCallId) return false;

        var expected = GuildTaskArguments.Creation(admission, 1, "cohortId", "expectedRevision");

        return expected.IsSuccess && (GuildTaskArguments.IsRemote(admission.Arguments) || request.AgentId is null)
            && JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(expected.Value with { ExpectedRevision = 1 }, GuildJsonContext.Default.GuildIssueChildRequest),
                JsonSerializer.SerializeToElement(request with { ExpectedRevision = 1, AgentId = null }, GuildJsonContext.Default.GuildIssueChildRequest));
    }

    internal static bool Revision(JsonElement arguments, out long revision)
    {
        revision = 0;

        return arguments.TryGetProperty("expectedRevision", out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out revision) && revision >= 1;
    }
}
