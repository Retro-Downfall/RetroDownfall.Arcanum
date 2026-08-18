using System.Text.Json;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Sanctum;

public sealed class SanctumConfigTests
{

    [Fact]
    public void AllowedPaths_IsReadOnly_NotADowncastableList()
    {

        SanctumConfig config = new() { AllowedPaths = ["/workspace"] };

        Assert.Equal(["/workspace"], config.AllowedPaths);

        // W3.6: the getter must not hand back the internal List<string> (which a consumer could
        // downcast and mutate, defeating the sandbox allow-list immutability).
        Assert.IsNotType<List<string>>(config.AllowedPaths);

        Assert.Throws<NotSupportedException>(() => ((IList<string>)config.AllowedPaths).Add("/escape"));

    }

    [Fact]
    public void AllowedDomains_IsReadOnly_NotADowncastableList()
    {

        SanctumConfig config = new() { AllowedDomains = ["example.com"] };

        Assert.IsNotType<List<string>>(config.AllowedDomains);

        Assert.Throws<NotSupportedException>(() => ((IList<string>)config.AllowedDomains).Add("evil.com"));

    }

    [Fact]
    public void DisabledTools_IsReadOnly_NotADowncastableList()
    {

        SanctumConfig config = new() { DisabledTools = ["execute_command"] };

        Assert.IsNotType<List<string>>(config.DisabledTools);

        Assert.Throws<NotSupportedException>(() => ((IList<string>)config.DisabledTools).Clear());

    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"enabled":true}""")]
    [InlineData("""{"enabled":true,"allowedPaths":["/workspace"]}""")]
    [InlineData("""{"enabled":true,"allowedPaths":null,"allowedDomains":null,"disabledTools":null}""")]
    public void Deserialize_PartialOrNullCollections_YieldsEmptyListsInsteadOfThrowing(string json)
    {

        // The source generator assigns every init-only member on every deserialization, passing null
        // for absent ones, so an incomplete payload must degrade to the most restrictive (empty) value
        // rather than throwing ArgumentNullException out of the setter.
        SanctumConfig? config = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.SanctumConfig);

        Assert.NotNull(config);

        Assert.NotNull(config.AllowedPaths);

        Assert.NotNull(config.AllowedDomains);

        Assert.NotNull(config.DisabledTools);

        Assert.Empty(config.AllowedDomains);

        Assert.Empty(config.DisabledTools);

    }

    [Fact]
    public void Deserialize_PartialPayload_PreservesSuppliedCollection()
    {

        SanctumConfig? config = JsonSerializer.Deserialize(
            """{"enabled":true,"allowedPaths":["/workspace"]}""",
            TheForgeJsonContext.Default.SanctumConfig);

        Assert.NotNull(config);

        Assert.True(config.Enabled);

        Assert.Equal(["/workspace"], config.AllowedPaths);

        Assert.Empty(config.AllowedDomains);

    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"enabled":true}""")]
    [InlineData("""{"enabled":true,"allowedPaths":["/workspace"]}""")]
    [InlineData("""{"enabled":true,"resourceLimits":{}}""")]
    public void Deserialize_PartialPayload_PreservesDeclaredDefaultsForOmittedMembers(string json)
    {

        // The source generator models every init-only member as a pseudo constructor parameter and
        // assigns all of them from an args array pre-filled with default(T), so an omitted member
        // discards its C# property initializer. A partial PUT body must not silently downgrade the
        // path boundary to off, floor the breach retention, or null out the resource limits.
        SanctumConfig? config = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.SanctumConfig);

        AssertDeclaredDefaults(config);

        // ArcanumJsonContext is the resolver the host inserts for body binding, so it is the context
        // that actually decides what PUT /api/campaigns/{id}/sanctum sees.
        AssertDeclaredDefaults(JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.SanctumConfig));

    }

    private static void AssertDeclaredDefaults(SanctumConfig? config)
    {

        Assert.NotNull(config);

        Assert.True(config.EnforcePathBoundary);

        Assert.Equal(1000, config.MaxBreachCount);

        Assert.NotNull(config.ResourceLimits);

        Assert.Equal(512, config.ResourceLimits.MaxProcessMemoryMb);

        Assert.Equal(10, config.ResourceLimits.MaxProcessCount);

        Assert.Equal(100, config.ResourceLimits.MaxFileWriteMb);

        Assert.Equal(300, config.ResourceLimits.ProcessTimeoutSeconds);

        // 0 means "unlimited" for the three OS-enforced ceilings, so losing their defaults removes
        // the limit rather than tightening it.
        Assert.Equal(30, config.ResourceLimits.MaxCpuSeconds);

        Assert.Equal(512, config.ResourceLimits.MaxMemoryMb);

        Assert.Equal(256, config.ResourceLimits.MaxFileDescriptors);

    }

    [Fact]
    public void Deserialize_ExplicitScalarValues_AreHonouredOverTheDeclaredDefaults()
    {

        SanctumConfig? config = JsonSerializer.Deserialize(
            """{"enabled":true,"enforcePathBoundary":false,"maxBreachCount":250,"resourceLimits":{"maxProcessCount":3}}""",
            TheForgeJsonContext.Default.SanctumConfig);

        Assert.NotNull(config);

        Assert.False(config.EnforcePathBoundary);

        Assert.Equal(250, config.MaxBreachCount);

        Assert.Equal(3, config.ResourceLimits.MaxProcessCount);

    }

    [Fact]
    public void Serialize_KeepsTheDocumentedWireNames()
    {

        string json = JsonSerializer.Serialize(new SanctumConfig(), TheForgeJsonContext.Default.SanctumConfig);

        Assert.Contains("\"enforcePathBoundary\":true", json, StringComparison.Ordinal);

        Assert.Contains("\"maxBreachCount\":1000", json, StringComparison.Ordinal);

        Assert.Contains("\"resourceLimits\":{", json, StringComparison.Ordinal);

    }

    [Fact]
    public void ExplicitNullResourceLimitsInitializer_YieldsTheDeclaredDefaults()
    {

        SanctumConfig config = new() { ResourceLimits = null! };

        Assert.NotNull(config.ResourceLimits);

        Assert.Equal(512, config.ResourceLimits.MaxProcessMemoryMb);

    }

    [Fact]
    public void ExplicitNullCollectionInitializer_YieldsEmptyList()
    {

        SanctumConfig config = new() { AllowedPaths = null!, AllowedDomains = null!, DisabledTools = null! };

        Assert.Empty(config.AllowedPaths);

        Assert.Empty(config.AllowedDomains);

        Assert.Empty(config.DisabledTools);

    }

}
