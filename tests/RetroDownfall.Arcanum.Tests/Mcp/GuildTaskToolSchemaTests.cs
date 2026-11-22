using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class GuildTaskToolSchemaTests
{
    [Fact]
    public void Creation_grant_schema_keeps_closed_nonfinancial_authority()
    {
        JsonElement creation = ArcanumInternalToolServer.BuildTaskToolSchema("create_agent_task");
        JsonElement grant = creation.GetProperty("properties").GetProperty("grant");
        JsonElement axes = grant.GetProperty("properties");

        Assert.False(grant.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["delegation", "expiresAtUtc", "memory", "tools", "workspaces"],
            axes.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["goal"], creation.GetProperty("required").EnumerateArray().Select(static item => item.GetString()));

        AssertClosedMembers(axes.GetProperty("tools").GetProperty("items"), "name", "sourceKind", "sourceRegistrationId");
        AssertClosedMembers(axes.GetProperty("workspaces").GetProperty("items"), "access", "relativeRoot", "workspaceId");

        JsonElement memory = axes.GetProperty("memory").GetProperty("items");
        AssertClosedMembers(memory, "operation", "scope", "store");

        JsonElement scope = memory.GetProperty("properties").GetProperty("scope");
        Assert.False(scope.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["id", "kind"],
            scope.GetProperty("properties").EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["kind"], scope.GetProperty("required").EnumerateArray().Select(static item => item.GetString()));

        JsonElement scopeVariants = scope.GetProperty("oneOf");
        Assert.Equal(2, scopeVariants.GetArrayLength());
        Assert.Equal("global", scopeVariants[0].GetProperty("properties").GetProperty("kind").GetProperty("const").GetString());
        Assert.Equal(["id"], scopeVariants[1].GetProperty("required").EnumerateArray().Select(static item => item.GetString()));

        Assert.Equal(["local", "remote", "cohort"],
            axes.GetProperty("delegation").GetProperty("items").GetProperty("enum").EnumerateArray().Select(static item => item.GetString()));
        Assert.Equal(["string", "null"],
            axes.GetProperty("expiresAtUtc").GetProperty("type").EnumerateArray().Select(static item => item.GetString()));
    }

    private static void AssertClosedMembers(JsonElement schema, params string[] expected)
    {
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(expected,
            schema.GetProperty("properties").EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(expected,
            schema.GetProperty("required").EnumerateArray().Select(static item => item.GetString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Continuation_schema_requires_revision_only_for_the_continuation_variant()
    {
        JsonElement schema = ArcanumInternalToolServer.BuildTaskToolSchema("send_agent_message");

        Assert.True(schema.TryGetProperty("oneOf", out JsonElement variants));

        Assert.Equal(2, variants.GetArrayLength());

        JsonElement continuation = variants.EnumerateArray().Single(item =>
            item.GetProperty("properties").GetProperty("kind").GetProperty("const").GetString() == "continuation");

        Assert.Contains(continuation.GetProperty("required").EnumerateArray(), item => item.GetString() == "expectedRevision");
    }
}
