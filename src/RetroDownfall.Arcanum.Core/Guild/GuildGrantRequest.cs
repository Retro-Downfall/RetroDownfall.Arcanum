using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Preserves caller intent: unspecified differs from an explicitly supplied null.</summary>
public readonly record struct GuildOptional<T>(bool IsSpecified, T Value)
{
    public static GuildOptional<T> Specified(T value) => new(true, value);
}

/// <summary>Optional child axes; the converter retains the flat public grant wire shape.</summary>
[JsonConverter(typeof(GuildRequestedGrantJsonConverter))]
public sealed record GuildRequestedGrant(
    GuildOptional<ImmutableArray<GuildWorkspaceGrant>?> Workspaces = default,
    GuildOptional<ImmutableArray<GuildToolGrant>?> Tools = default,
    GuildOptional<ImmutableArray<GuildMemoryGrant>?> Memory = default,
    GuildOptional<GuildBudgetGrant?> Budget = default,
    GuildOptional<ImmutableArray<GuildDelegationKind>?> Delegation = default,
    GuildOptional<DateTimeOffset?> ExpiresAtUtc = default);

/// <summary>AOT-safe omission-preserving converter; it creates data, never a verified handle.</summary>
public sealed class GuildRequestedGrantJsonConverter : JsonConverter<GuildRequestedGrant>
{
    public override GuildRequestedGrant Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A requested Guild grant must be an object.");
        }

        GuildRequestedGrant grant = new();

        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new JsonException("A requested Guild grant contains a duplicate axis.");
            }

            grant = property.Name switch
            {
                "workspaces" => grant with { Workspaces = new(true, ReadArray(property.Value, GuildJsonContext.Default.GuildWorkspaceGrant)) },
                "tools" => grant with { Tools = new(true, ReadArray(property.Value, GuildJsonContext.Default.GuildToolGrant)) },
                "memory" => grant with { Memory = new(true, ReadArray(property.Value, GuildJsonContext.Default.GuildMemoryGrant)) },
                "budget" => grant with { Budget = new(true, property.Value.Deserialize(GuildJsonContext.Default.GuildBudgetGrant)) },
                "delegation" => grant with { Delegation = new(true, ReadArray(property.Value, GuildJsonContext.Default.GuildDelegationKind)) },
                "expiresAtUtc" => grant with { ExpiresAtUtc = new(true, property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetDateTimeOffset()) },
                _ => throw new JsonException("A requested Guild grant contains an unknown axis."),
            };
        }

        return grant;
    }

    public override void Write(Utf8JsonWriter writer, GuildRequestedGrant value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        WriteArray(writer, "workspaces", value.Workspaces, GuildJsonContext.Default.GuildWorkspaceGrant);

        WriteArray(writer, "tools", value.Tools, GuildJsonContext.Default.GuildToolGrant);

        WriteArray(writer, "memory", value.Memory, GuildJsonContext.Default.GuildMemoryGrant);

        if (value.Budget.IsSpecified)
        {
            writer.WritePropertyName("budget");

            JsonSerializer.Serialize(writer, value.Budget.Value, GuildJsonContext.Default.GuildBudgetGrant);
        }

        WriteArray(writer, "delegation", value.Delegation, GuildJsonContext.Default.GuildDelegationKind);

        if (value.ExpiresAtUtc.IsSpecified)
        {
            if (value.ExpiresAtUtc.Value is { } expiry)
            {
                writer.WriteString("expiresAtUtc", expiry);
            }
            else
            {
                writer.WriteNull("expiresAtUtc");
            }
        }

        writer.WriteEndObject();
    }

    private static ImmutableArray<T>? ReadArray<T>(JsonElement element, JsonTypeInfo<T> typeInfo)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("A supplied Guild grant set must be an array.");
        }

        ImmutableArray<T>.Builder items = ImmutableArray.CreateBuilder<T>();

        foreach (JsonElement item in element.EnumerateArray())
        {
            items.Add(item.Deserialize(typeInfo)!);
        }

        return items.ToImmutable();
    }

    private static void WriteArray<T>(Utf8JsonWriter writer, string name, GuildOptional<ImmutableArray<T>?> axis, JsonTypeInfo<T> typeInfo)
    {
        if (!axis.IsSpecified)
        {
            return;
        }

        writer.WritePropertyName(name);

        if (axis.Value is not { } items)
        {
            writer.WriteNullValue();

            return;
        }

        if (items.IsDefault)
        {
            throw new JsonException("A supplied Guild grant set must be initialized.");
        }

        writer.WriteStartArray();

        foreach (T item in items)
        {
            JsonSerializer.Serialize(writer, item, typeInfo);
        }

        writer.WriteEndArray();
    }
}
