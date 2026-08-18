using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// <see cref="ToolPolicy"/> governs which tools a turn advertises, and every consumer treats an
/// unrecognized member as the permissive arm. A numeric wire value that binds to an undefined
/// member would therefore fail open — silently discarding <c>disableMcpTools</c> and skipping the
/// advertisement filters — so the contract is string-only and rejects integers at the boundary.
/// </summary>
public sealed class ToolPolicyJsonTests
{

    [Theory]
    [InlineData(ToolPolicy.AllTools, "\"allTools\"")]
    [InlineData(ToolPolicy.NoTools, "\"noTools\"")]
    [InlineData(ToolPolicy.ReadOnlyTools, "\"readOnlyTools\"")]
    [InlineData(ToolPolicy.NoForbiddenArts, "\"noForbiddenArts\"")]
    public void Policy_UsesStableNormalizedWireName(ToolPolicy policy, string expectedJson)
    {

        string json = JsonSerializer.Serialize(policy, ArcanumJsonContext.Default.ToolPolicy);

        Assert.Equal(expectedJson, json);

    }

    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("7")]
    [InlineData("-1")]
    public void Policy_SourceGeneratedContractRejectsDefinedAndUndefinedIntegers(string json)
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ToolPolicy));
    }

    [Theory]
    [InlineData("""{"prompt":"hello","toolPolicy":0}""")]
    [InlineData("""{"prompt":"hello","disableMcpTools":true,"toolPolicy":7}""")]
    public void PingRequest_RejectsNumericToolPolicy(string json)
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.PingRequest));
    }

    [Fact]
    public void SpellExecuteRequest_RejectsNumericToolPolicy()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(
                """{"toolPolicy":7}""",
                ArcanumJsonContext.Default.SpellExecuteRequest));
    }

    [Fact]
    public void PromptExecuteRequest_RejectsNumericToolPolicy()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(
                """{"toolPolicy":7}""",
                ArcanumJsonContext.Default.PromptExecuteRequest));
    }

    [Fact]
    public void PingRequest_AcceptsCanonicalToolPolicyString()
    {

        PingRequest? request = JsonSerializer.Deserialize(
            """{"prompt":"hello","toolPolicy":"noForbiddenArts"}""",
            ArcanumJsonContext.Default.PingRequest);

        Assert.NotNull(request);

        Assert.Equal(ToolPolicy.NoForbiddenArts, request.ToolPolicy);

    }

    [Fact]
    public void PingRequest_RejectsUnknownToolPolicyString()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(
                """{"prompt":"hello","toolPolicy":"everything"}""",
                ArcanumJsonContext.Default.PingRequest));
    }

}
