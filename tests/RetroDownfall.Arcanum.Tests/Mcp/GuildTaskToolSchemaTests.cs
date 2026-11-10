using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class GuildTaskToolSchemaTests
{
    [Fact]
    public void Creation_grant_schema_describes_closed_exact_scope_and_budget_axes()
    {
        JsonElement grant = ArcanumInternalToolServer.BuildTaskToolSchema("create_agent_task")
            .GetProperty("properties").GetProperty("grant").GetProperty("properties");

        Assert.True(grant.GetProperty("tools").GetProperty("items").TryGetProperty("properties", out JsonElement tools));

        Assert.Equal(["name", "sourceKind", "sourceRegistrationId"], tools.EnumerateObject().Select(p => p.Name).Order().ToArray());

        Assert.True(grant.GetProperty("workspaces").GetProperty("items").TryGetProperty("properties", out JsonElement workspaces));

        Assert.Equal(["access", "relativeRoot", "workspaceId"], workspaces.EnumerateObject().Select(p => p.Name).Order().ToArray());

        Assert.True(grant.GetProperty("memory").GetProperty("items").TryGetProperty("properties", out JsonElement memory));

        Assert.Equal(["operation", "scope", "store"], memory.EnumerateObject().Select(p => p.Name).Order().ToArray());

        Assert.True(grant.GetProperty("budget").TryGetProperty("properties", out JsonElement budget));

        Assert.Equal(["tokens", "usd"], budget.EnumerateObject().Select(p => p.Name).Order().ToArray());

        foreach (string axis in new[] { "tokens", "usd" })
        {
            Assert.False(budget.GetProperty(axis).GetProperty("additionalProperties").GetBoolean());

            Assert.Equal(["mode", "value"], budget.GetProperty(axis).GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray());
        }
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
