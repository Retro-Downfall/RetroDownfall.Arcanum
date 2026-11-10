using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Canonical caller intent only; no host resolution or authority is derived here.</summary>
public static class GuildQuestIntent
{
    private const int MaximumCanonicalBytes = 65_536;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string NormalizeGoal(string goal) =>
        goal.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Normalize(NormalizationForm.FormC);

    public static Result<string> Compute(Guid questId, GuildCancelQuestRequest request) => ComputeCanonical(writer =>
    {
        if (questId == Guid.Empty || request is null || request.ExpectedRevision < 1)
            throw new InvalidIntentException();

        writer.WriteString("kind", "cancel");

        writer.WriteString("questId", questId);

        writer.WriteNumber("expectedRevision", request.ExpectedRevision);

        writer.WritePropertyName("reason");

        WriteEnum(writer, request.ReasonCode);
    });

    public static Result<string> Compute(Guid questId, GuildContinueRequest request) => ComputeCanonical(writer =>
    {
        if (questId == Guid.Empty || request is null || request.ExpectedRevision < 1 || request.ContinuationId == Guid.Empty
            || request.Body is null || request.MimeType is not ("text/plain" or "application/json"))
            throw new InvalidIntentException();

        writer.WriteString("kind", "continue");

        writer.WriteString("questId", questId);

        writer.WriteNumber("expectedRevision", request.ExpectedRevision);

        writer.WriteString("continuationId", request.ContinuationId);

        writer.WriteString("mimeType", request.MimeType);

        WriteText(writer, "body", request.Body);

        WriteRemoteOptions(writer, request.RemoteOptions);
    });

    public static Result<string> Compute(GuildIssueRootRequest request) => ComputeCanonical(writer =>
    {
        if (request is null || request.Goal is null || request.Grant is null)
            throw new InvalidIntentException();

        writer.WriteString("kind", "root");

        WriteIdentity(writer, "agentId", request.AgentId);

        WriteText(writer, "name", request.Name);

        WriteText(writer, "goal", NormalizeGoal(request.Goal));

        WriteIdentity(writer, "sessionId", request.SessionId);

        WriteIdentity(writer, "campaignId", request.CampaignId);

        WriteIdentity(writer, "workspaceId", request.WorkspaceId);

        WriteIdentity(writer, "predecessorQuestId", request.PredecessorQuestId);

        writer.WritePropertyName("grant");

        WriteGrant(writer, request.Grant);

        WriteRemoteOptions(writer, request.RemoteOptions);
    });

    public static Result<string> Compute(Guid parentQuestId, GuildIssueChildRequest request) => ComputeCanonical(writer =>
    {
        if (request is null || request.Goal is null)
            throw new InvalidIntentException();

        writer.WriteString("kind", "child");

        writer.WriteString("parentQuestId", parentQuestId);

        writer.WriteNumber("expectedRevision", request.ExpectedRevision);

        WriteIdentity(writer, "agentId", request.AgentId);

        if (request.CardId is not null) WriteIdentity(writer, "cardId", request.CardId);

        WriteText(writer, "name", request.Name);

        WriteText(writer, "goal", NormalizeGoal(request.Goal));

        writer.WritePropertyName("requestedGrant");

        if (request.RequestedGrant is null)
            writer.WriteNullValue();
        else
            WriteRequestedGrant(writer, request.RequestedGrant);

        WriteRemoteOptions(writer, request.RemoteOptions);
    });

    internal static void WriteRemoteOptions(Utf8JsonWriter writer, GuildRemoteInvocationOptions? options)
    {
        // Omitted options retain the existing version-one caller-intent representation.
        if (options is null) return;

        if (!Enum.IsDefined(options.Mode) || !options.AcceptedOutputModes.IsDefault
            && options.AcceptedOutputModes.Any(string.IsNullOrWhiteSpace)) throw new InvalidIntentException();

        writer.WritePropertyName("remoteOptions");

        writer.WriteStartObject();

        writer.WritePropertyName("mode");

        WriteEnum(writer, options.Mode);

        writer.WritePropertyName("acceptedOutputModes");

        if (options.AcceptedOutputModes.IsDefault) writer.WriteNullValue();
        else
        {
            writer.WriteStartArray();

            foreach (string mode in options.AcceptedOutputModes) writer.WriteStringValue(mode);

            writer.WriteEndArray();
        }

        WriteText(writer, "skillId", options.SkillId);

        writer.WriteEndObject();
    }

    private static Result<string> ComputeCanonical(Action<Utf8JsonWriter> write)
    {
        try
        {
            byte[] canonical = Encode(writer =>
            {
                writer.WriteStartObject();

                writer.WriteNumber("version", 1);

                write(writer);

                writer.WriteEndObject();
            });

            return Convert.ToHexString(SHA256.HashData(canonical));
        }
        catch (IntentTooLargeException)
        {
            return new Error(ErrorCodes.Guild.PayloadTooLarge, "The canonical Quest request exceeds its materialization boundary.");
        }
        catch (Exception exception) when (exception is InvalidIntentException or ArgumentException)
        {
            return new Error(ErrorCodes.Guild.InvalidRequest, "The Quest request contains malformed caller intent.");
        }
    }

    private static byte[] Encode(Action<Utf8JsonWriter> write)
    {
        using BoundedIntentStream stream = new();

        using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        write(writer);

        writer.Flush();

        return stream.ToArray();
    }

    private static void WriteGrant(Utf8JsonWriter writer, GuildGrant grant)
    {
        writer.WriteStartObject();

        WriteSet(writer, "workspaces", grant.Workspaces, WriteWorkspace);

        WriteSet(writer, "tools", grant.Tools, WriteTool);

        WriteSet(writer, "memory", grant.Memory, WriteMemory);

        writer.WritePropertyName("budget");

        WriteBudget(writer, grant.Budget);

        WriteSet(writer, "delegation", grant.Delegation, static (output, value) => WriteEnum(output, value));

        WriteExpiry(writer, grant.ExpiresAtUtc);

        writer.WriteEndObject();
    }

    private static void WriteRequestedGrant(Utf8JsonWriter writer, GuildRequestedGrant grant)
    {
        writer.WriteStartObject();

        WriteOptionalSet(writer, "workspaces", grant.Workspaces, WriteWorkspace);

        WriteOptionalSet(writer, "tools", grant.Tools, WriteTool);

        WriteOptionalSet(writer, "memory", grant.Memory, WriteMemory);

        if (grant.Budget.IsSpecified)
        {
            writer.WritePropertyName("budget");

            if (grant.Budget.Value is null)
                writer.WriteNullValue();
            else
                WriteBudget(writer, grant.Budget.Value);
        }

        WriteOptionalSet(writer, "delegation", grant.Delegation, static (output, value) => WriteEnum(output, value));

        if (grant.ExpiresAtUtc.IsSpecified)
            WriteExpiry(writer, grant.ExpiresAtUtc.Value);

        writer.WriteEndObject();
    }

    private static void WriteOptionalSet<T>(Utf8JsonWriter writer, string name, GuildOptional<ImmutableArray<T>?> axis, Action<Utf8JsonWriter, T> write)
    {
        if (!axis.IsSpecified)
            return;

        if (axis.Value is { } values)
            WriteSet(writer, name, values, write);
        else
            writer.WriteNull(name);
    }

    private static void WriteSet<T>(Utf8JsonWriter writer, string name, ImmutableArray<T> values, Action<Utf8JsonWriter, T> write)
    {
        if (values.IsDefault)
            throw new InvalidIntentException();

        HashSet<string> unique = new(StringComparer.Ordinal);

        long minimumBytes = 0;

        foreach (T value in values)
        {
            byte[] encoded = Encode(output => write(output, value));

            if (unique.Add(StrictUtf8.GetString(encoded)))
                minimumBytes += encoded.Length + 1L;

            if (minimumBytes > MaximumCanonicalBytes)
                throw new IntentTooLargeException();
        }

        writer.WriteStartArray(name);

        foreach (string value in unique.Order(StringComparer.Ordinal))
            writer.WriteRawValue(value);

        writer.WriteEndArray();
    }

    private static void WriteWorkspace(Utf8JsonWriter writer, GuildWorkspaceGrant workspace)
    {
        if (workspace is null || workspace.RelativeRoot is null)
            throw new InvalidIntentException();

        writer.WriteStartObject();

        writer.WriteString("workspaceId", workspace.WorkspaceId);

        WriteText(writer, "relativeRoot", workspace.RelativeRoot == "" ? "." : workspace.RelativeRoot);

        writer.WritePropertyName("access");

        WriteEnum(writer, workspace.Access);

        writer.WriteEndObject();
    }

    private static void WriteTool(Utf8JsonWriter writer, GuildToolGrant tool)
    {
        if (tool is null || tool.SourceRegistrationId is null || tool.Name is null)
            throw new InvalidIntentException();

        writer.WriteStartObject();

        writer.WritePropertyName("sourceKind");

        WriteEnum(writer, tool.SourceKind);

        WriteText(writer, "sourceRegistrationId", tool.SourceRegistrationId);

        WriteText(writer, "name", tool.Name);

        writer.WriteEndObject();
    }

    private static void WriteMemory(Utf8JsonWriter writer, GuildMemoryGrant memory)
    {
        if (memory is null || memory.Scope is null)
            throw new InvalidIntentException();

        writer.WriteStartObject();

        writer.WritePropertyName("store");

        WriteEnum(writer, memory.Store);

        writer.WritePropertyName("operation");

        WriteEnum(writer, memory.Operation);

        writer.WritePropertyName("scopeKind");

        WriteEnum(writer, memory.Scope.Kind);

        WriteIdentity(writer, "scopeId", memory.Scope.Id);

        writer.WriteEndObject();
    }

    private static void WriteBudget(Utf8JsonWriter writer, GuildBudgetGrant budget)
    {
        if (budget is null || budget.Usd is null || budget.Tokens is null)
            throw new InvalidIntentException();

        writer.WriteStartObject();

        writer.WritePropertyName("usdMode");

        WriteEnum(writer, budget.Usd.Mode);

        writer.WritePropertyName("usdValue");

        if (budget.Usd.Value is { } usd)
            writer.WriteRawValue(usd.ToString("G29", CultureInfo.InvariantCulture));
        else
            writer.WriteNullValue();

        writer.WritePropertyName("tokensMode");

        WriteEnum(writer, budget.Tokens.Mode);

        if (budget.Tokens.Value is { } tokens)
            writer.WriteNumber("tokensValue", tokens);
        else
            writer.WriteNull("tokensValue");

        writer.WriteEndObject();
    }

    private static void WriteExpiry(Utf8JsonWriter writer, DateTimeOffset? expiry)
    {
        if (expiry is { } value)
            writer.WriteString("expiresAtUtc", value.ToUniversalTime());
        else
            writer.WriteNull("expiresAtUtc");
    }

    private static void WriteIdentity(Utf8JsonWriter writer, string name, Guid? identity)
    {
        if (identity is { } value)
            writer.WriteString(name, value);
        else
            writer.WriteNull(name);
    }

    private static void WriteText(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null && StrictUtf8.GetByteCount(value) > MaximumCanonicalBytes)
            throw new IntentTooLargeException();

        writer.WriteString(name, value);
    }

    private static void WriteEnum<T>(Utf8JsonWriter writer, T value) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new InvalidIntentException();

        writer.WriteNumberValue(Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }

    private sealed class BoundedIntentStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position + count > MaximumCanonicalBytes)
                throw new IntentTooLargeException();

            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position + buffer.Length > MaximumCanonicalBytes)
                throw new IntentTooLargeException();

            // MemoryStream's span fallback dispatches to the array override for derived types.
            // Call its array implementation directly, avoiding a virtual span/array cycle.
            base.Write(buffer.ToArray(), 0, buffer.Length);
        }
    }

    private sealed class InvalidIntentException : Exception { }

    private sealed class IntentTooLargeException : Exception { }
}
