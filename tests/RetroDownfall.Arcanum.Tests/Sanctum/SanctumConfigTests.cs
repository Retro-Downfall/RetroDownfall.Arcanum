using System.Text.Json;

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

    [Fact]
    public void ExplicitNullCollectionInitializer_YieldsEmptyList()
    {

        SanctumConfig config = new() { AllowedPaths = null!, AllowedDomains = null!, DisabledTools = null! };

        Assert.Empty(config.AllowedPaths);

        Assert.Empty(config.AllowedDomains);

        Assert.Empty(config.DisabledTools);

    }

}
