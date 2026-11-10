using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Closed model input. Host identity and execution authority have no argument path.</summary>
internal static class GuildTaskArguments
{
    internal static bool IsRemote(JsonElement? arguments) => arguments is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty("agent", out JsonElement agent) && agent.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(agent.GetString());

    internal static Result<GuildIssueChildRequest> Creation(GuildLocalChildToolCall call)
    {
        JsonElement arguments = call.Admission.Arguments;

        if (!Closed(arguments, "goal", "name", "agent", "cardId", "grant")
            || !Text(arguments, "goal", out string? goal) || string.IsNullOrWhiteSpace(goal)
            || !OptionalText(arguments, "name", out string? name)
            || !OptionalText(arguments, "agent", out string? agent)
            || arguments.TryGetProperty("agent", out _) && string.IsNullOrWhiteSpace(agent)) return Invalid;

        Guid? card = null;

        if (arguments.TryGetProperty("cardId", out JsonElement selected))
        {
            if (selected.ValueKind != JsonValueKind.String || !selected.TryGetGuid(out Guid id) || id == Guid.Empty) return Invalid;

            card = id;
        }

        if (card is not null && agent is not null) return Invalid;

        try
        {
            GuildRequestedGrant? grant = arguments.TryGetProperty("grant", out JsonElement requested)
                ? requested.Deserialize(GuildJsonContext.Default.GuildRequestedGrant) : null;

            if (arguments.TryGetProperty("grant", out _) && grant is null) return Invalid;

            return new GuildIssueChildRequest(call.Admission.LogicalCallId, call.ParentQuest.Revision, null,
                name, goal, grant, agent is null ? null : new(GuildRemoteDispatchMode.Continuable), card);
        }
        catch (JsonException)
        {
            return Invalid;
        }
    }

    internal static bool Closed(JsonElement arguments, params string[] names) => arguments.ValueKind == JsonValueKind.Object
        && arguments.EnumerateObject().All(property => names.Contains(property.Name, StringComparer.Ordinal));

    internal static bool Text(JsonElement arguments, string name, out string? value)
    {
        value = null;

        if (!arguments.TryGetProperty(name, out JsonElement text) || text.ValueKind != JsonValueKind.String) return false;

        value = text.GetString();

        return true;
    }

    internal static bool OptionalText(JsonElement arguments, string name, out string? value)
    {
        value = null;

        return !arguments.TryGetProperty(name, out _) || Text(arguments, name, out value);
    }

    internal static bool Identity(JsonElement arguments, string name, out Guid value)
    {
        value = Guid.Empty;

        return arguments.TryGetProperty(name, out JsonElement item) && item.ValueKind == JsonValueKind.String
            && item.TryGetGuid(out value) && value != Guid.Empty;
    }

    internal static Result<Guid> Mutation(JsonElement arguments, Guid fallback) => !arguments.TryGetProperty("idempotencyKey", out _)
        ? fallback : Identity(arguments, "idempotencyKey", out Guid value) ? value : Invalid;

    internal static Error Invalid => new(ErrorCodes.Guild.InvalidRequest, "The task tool requires supported typed arguments.");
}
