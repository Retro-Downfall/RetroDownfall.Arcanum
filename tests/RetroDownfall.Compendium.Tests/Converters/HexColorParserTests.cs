using RetroDownfall.Compendium.Ux.Converters;

using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Converters;

public sealed class HexColorParserTests
{

    [Theory]

    [InlineData("#000", 255, 0, 0, 0)]

    [InlineData("#fff", 255, 255, 255, 255)]

    [InlineData("#000000", 255, 0, 0, 0)]

    [InlineData("#ffffff", 255, 255, 255, 255)]

    [InlineData("#00ff84", 255, 0, 255, 132)]

    [InlineData("00ff84", 255, 0, 255, 132)]

    public void TryParse_parses_rgb_hex(string hex, byte a, byte r, byte g, byte b)
    {

        bool parsed = HexColorParser.TryParse(hex, out byte parsedA, out byte parsedR, out byte parsedG, out byte parsedB);

        Assert.True(parsed);

        Assert.Equal(a, parsedA);

        Assert.Equal(r, parsedR);

        Assert.Equal(g, parsedG);

        Assert.Equal(b, parsedB);

    }

    [Fact]

    public void TryParse_parses_argb_hex()
    {

        bool parsed = HexColorParser.TryParse("#80FF0000", out byte a, out byte r, out byte g, out byte b);

        Assert.True(parsed);

        Assert.Equal(128, a);

        Assert.Equal(255, r);

        Assert.Equal(0, g);

        Assert.Equal(0, b);

    }

    [Theory]

    [InlineData("")]

    [InlineData("   ")]

    [InlineData("not-a-color")]

    [InlineData("#ZZZ")]

    [InlineData("#12")]

    [InlineData("#12345")]

    public void TryParse_returns_false_for_invalid_input(string hex)
    {

        bool parsed = HexColorParser.TryParse(hex, out _, out _, out _, out _);

        Assert.False(parsed);

    }

}
