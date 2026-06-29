using RetroDownfall.Arcanum.Core.Sanctum;

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

}
