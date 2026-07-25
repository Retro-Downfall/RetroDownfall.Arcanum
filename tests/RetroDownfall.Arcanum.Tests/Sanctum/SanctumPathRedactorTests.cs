using RetroDownfall.Arcanum.Core.Sanctum;

namespace RetroDownfall.Arcanum.Tests.Sanctum;

public sealed class SanctumPathRedactorTests
{

    [Fact]
    public void Redact_NormalPath_ReturnsFileNameOnly()
    {

        string? redacted = SanctumPathRedactor.Redact("/outside/secret.txt");

        Assert.Equal("secret.txt", redacted);

    }

    [Fact]
    public void Redact_Null_ReturnsNull()
    {

        Assert.Null(SanctumPathRedactor.Redact(null));

    }

    [Fact]
    public void Redact_Whitespace_ReturnsInputUnchanged()
    {

        Assert.Equal("   ", SanctumPathRedactor.Redact("   "));

    }

    [Fact]
    public void Redact_PathWithEmbeddedNull_ReturnsRedactedPlaceholder()
    {

        string? redacted = SanctumPathRedactor.Redact("bad\u0000path");

        Assert.Equal("[redacted]", redacted);

    }

}
