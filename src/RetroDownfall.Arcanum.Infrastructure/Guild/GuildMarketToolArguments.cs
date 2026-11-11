using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal static class GuildMarketToolArguments
{
    internal static Result<PostWorkOfferRequest> Post(GuildChildToolAdmission a)
    {
        JsonElement x = a.Arguments;

        if (a.Kind != GuildLocalChildToolKind.PostWorkOffer || !GuildTaskArguments.Closed(x, "cohortId", "expectedCohortRevision", "skillId", "goal", "resultType", "grant")
            || !Number(x, "expectedCohortRevision", out long revision) || !GuildTaskArguments.Text(x, "skillId", out string? skill) || string.IsNullOrWhiteSpace(skill)
            || !GuildTaskArguments.Text(x, "goal", out string? goal) || string.IsNullOrWhiteSpace(goal)
            || !GuildTaskArguments.Text(x, "resultType", out string? mime) || mime is not ("text/plain" or "application/json")) return GuildTaskArguments.Invalid;

        try
        {
            GuildRequestedGrant? grant = null;

            if (x.TryGetProperty("grant", out JsonElement raw))
            {
                JsonElement schema = ArcanumInternalToolServer.BuildTaskToolSchema("create_agent_task").GetProperty("properties").GetProperty("grant");

                if (!ClosedNested(raw, schema)) return GuildTaskArguments.Invalid;

                grant = raw.Deserialize(GuildJsonContext.Default.GuildRequestedGrant);

                if (grant is null) return GuildTaskArguments.Invalid;
            }

            return new PostWorkOfferRequest(a.LogicalCallId, revision, skill, goal, grant, new(mime));
        }
        catch (JsonException) { return GuildTaskArguments.Invalid; }
    }

    internal static Result<SubmitBidRequest> Submit(GuildChildToolAdmission a)
    {
        JsonElement x = a.Arguments;

        if (a.Kind != GuildLocalChildToolKind.SubmitBid || !GuildTaskArguments.Closed(x, "offerId", "expectedRevision", "note", "claimedCostUsd")
            || !Number(x, "expectedRevision", out long revision) || !GuildTaskArguments.Text(x, "note", out string? note) || note is null) return GuildTaskArguments.Invalid;

        decimal? cost = null;

        if (x.TryGetProperty("claimedCostUsd", out JsonElement value))
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out decimal number) || number < 0) return GuildTaskArguments.Invalid;

            cost = number;
        }

        return new SubmitBidRequest(a.LogicalCallId, revision, note, cost);
    }

    internal static Result<AwardWorkRequest> Award(GuildChildToolAdmission a)
    {
        JsonElement x = a.Arguments;

        if (a.Kind != GuildLocalChildToolKind.AwardWork || !GuildTaskArguments.Closed(x, "offerId", "expectedRevision", "expectedCohortRevision", "bidId")
            || !Number(x, "expectedRevision", out long revision) || !Number(x, "expectedCohortRevision", out long cohortRevision)
            || !GuildTaskArguments.Identity(x, "bidId", out Guid bid)) return GuildTaskArguments.Invalid;

        return new AwardWorkRequest(a.LogicalCallId, revision, cohortRevision, bid);
    }

    internal static Result<GuildRevisionRequest> Withdraw(GuildChildToolAdmission a)
    {
        string id = a.Kind == GuildLocalChildToolKind.WithdrawBid ? "bidId" : "offerId";

        if (a.Kind is not (GuildLocalChildToolKind.WithdrawBid or GuildLocalChildToolKind.WithdrawWorkOffer)
            || !GuildTaskArguments.Closed(a.Arguments, id, "expectedRevision") || !Number(a.Arguments, "expectedRevision", out long revision)) return GuildTaskArguments.Invalid;

        return new GuildRevisionRequest(a.LogicalCallId, revision);
    }

    internal static Result<AppendSharedNoteRequest> Append(GuildChildToolAdmission a)
    {
        JsonElement x = a.Arguments;

        if (a.Kind != GuildLocalChildToolKind.AppendSharedNote || !GuildTaskArguments.Closed(x, "cohortId", "expectedRevision", "kind", "body", "subjectId", "correctsNoteId")
            || !Number(x, "expectedRevision", out long revision) || !GuildTaskArguments.Text(x, "kind", out string? kind)
            || !GuildTaskArguments.Text(x, "body", out string? body) || body is null
            || !OptionalIdentity(x, "subjectId", out Guid? subject) || !OptionalIdentity(x, "correctsNoteId", out Guid? corrects)) return GuildTaskArguments.Invalid;

        GuildNoteKind noteKind = kind switch { "observation" => GuildNoteKind.Observation, "proposal" => GuildNoteKind.Proposal, "correction" => GuildNoteKind.Correction, _ => 0 };

        if (noteKind == 0) return GuildTaskArguments.Invalid;

        return new AppendSharedNoteRequest(a.LogicalCallId, revision, noteKind, subject, body, corrects);
    }

    internal static Result<GuildPageQuery> Read(GuildChildToolAdmission a)
    {
        JsonElement x = a.Arguments;

        if (a.Kind != GuildLocalChildToolKind.ReadSharedNotes || !GuildTaskArguments.Closed(x, "cohortId", "cursor", "pageSize")
            || !GuildTaskArguments.OptionalText(x, "cursor", out string? cursor)) return GuildTaskArguments.Invalid;

        int size = 25;

        if (x.TryGetProperty("pageSize", out JsonElement raw) && (raw.ValueKind != JsonValueKind.Number || !raw.TryGetInt32(out size) || size is < 1 or > 100)) return GuildTaskArguments.Invalid;

        return new GuildPageQuery(cursor, size);
    }

    internal static bool MatchesPost(GuildChildToolAdmission a, Guid id, PostWorkOfferRequest request) => Target(a, "cohortId", id) && Equal(Post(a), request, GuildJsonContext.Default.PostWorkOfferRequest);

    internal static bool MatchesSubmit(GuildChildToolAdmission a, Guid id, SubmitBidRequest request) => Target(a, "offerId", id) && Equal(Submit(a), request, GuildJsonContext.Default.SubmitBidRequest);

    internal static bool MatchesAward(GuildChildToolAdmission a, Guid id, AwardWorkRequest request) => Target(a, "offerId", id) && Equal(Award(a), request, GuildJsonContext.Default.AwardWorkRequest);

    internal static bool MatchesWithdrawOffer(GuildChildToolAdmission a, Guid id, GuildRevisionRequest request) => a.Kind == GuildLocalChildToolKind.WithdrawWorkOffer && Target(a, "offerId", id) && Equal(Withdraw(a), request, GuildJsonContext.Default.GuildRevisionRequest);

    internal static bool MatchesWithdrawBid(GuildChildToolAdmission a, Guid id, GuildRevisionRequest request) => a.Kind == GuildLocalChildToolKind.WithdrawBid && Target(a, "bidId", id) && Equal(Withdraw(a), request, GuildJsonContext.Default.GuildRevisionRequest);

    internal static bool MatchesAppend(GuildChildToolAdmission a, Guid id, AppendSharedNoteRequest request) => Target(a, "cohortId", id) && Equal(Append(a), request, GuildJsonContext.Default.AppendSharedNoteRequest);

    internal static bool MatchesRead(GuildChildToolAdmission a, Guid id, GuildPageQuery request) => Target(a, "cohortId", id) && Read(a) is { IsSuccess: true } parsed && parsed.Value == request;

    private static bool Target(GuildChildToolAdmission a, string field, Guid id) => GuildTaskArguments.Identity(a.Arguments, field, out Guid found) && found == id;

    private static bool Equal<T>(Result<T> expected, T actual, JsonTypeInfo<T> type) => expected.IsSuccess && actual is not null
        && JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected.Value, type), JsonSerializer.SerializeToElement(actual, type));

    private static bool Number(JsonElement x, string name, out long value)
    {
        value = 0;

        return x.TryGetProperty(name, out JsonElement raw) && raw.ValueKind == JsonValueKind.Number && raw.TryGetInt64(out value) && value > 0;
    }

    private static bool OptionalIdentity(JsonElement x, string name, out Guid? value)
    {
        value = null;

        if (!x.TryGetProperty(name, out _)) return true;

        if (!GuildTaskArguments.Identity(x, name, out Guid id)) return false;

        value = id;

        return true;
    }

    private static bool ClosedNested(JsonElement value, JsonElement schema)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (!schema.TryGetProperty("properties", out JsonElement properties)) return false;

            foreach (JsonProperty property in value.EnumerateObject())
                if (!properties.TryGetProperty(property.Name, out JsonElement child) || !ClosedNested(property.Value, child)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            if (!schema.TryGetProperty("items", out JsonElement items)) return false;

            foreach (JsonElement item in value.EnumerateArray()) if (!ClosedNested(item, items)) return false;
        }

        return true;
    }
}
