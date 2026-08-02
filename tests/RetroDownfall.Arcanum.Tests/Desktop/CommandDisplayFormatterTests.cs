using RetroDownfall.Arcanum.Core.Desktop;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Desktop;

public sealed class CommandDisplayFormatterTests
{

    private const string HostileArgument =
        "--O'Reilly $(touch /tmp/not-created) `uname` \"\u96ea\"";

    [Theory]

    [InlineData(CommandDisplayPlatform.MacOS)]

    [InlineData(CommandDisplayPlatform.Linux)]

    public void QuoteArgument_PosixShell_UsesSingleQuotesAndEscapesApostrophes(
        CommandDisplayPlatform platform)
    {

        const string expected =
            "'--O'\"'\"'Reilly $(touch /tmp/not-created) `uname` \"\u96ea\"'";

        string rendered = CommandDisplayFormatter.QuoteArgument(
            HostileArgument,
            platform);

        Assert.Equal(expected, rendered);

    }

    [Fact]

    public void QuoteArgument_WindowsPowerShell_DoublesApostrophes()
    {

        const string expected =
            "'--O''Reilly $(touch /tmp/not-created) `uname` \"\u96ea\"'";

        string rendered = CommandDisplayFormatter.QuoteArgument(
            HostileArgument,
            CommandDisplayPlatform.Windows);

        Assert.Equal(expected, rendered);

    }

    [Theory]

    [InlineData("@spell", "'@spell'")]

    [InlineData(",spell", "',spell'")]

    public void QuoteArgument_WindowsPowerShell_QuotesParserPrefixes(
        string value,
        string expected)
    {

        Assert.Equal(
            expected,
            CommandDisplayFormatter.QuoteArgument(
                value,
                CommandDisplayPlatform.Windows));

    }

    [Theory]

    [InlineData("_spell")]

    [InlineData("%spell")]

    [InlineData("+spell")]

    [InlineData("=spell")]

    [InlineData(":spell")]

    [InlineData(".spell")]

    [InlineData("/spell")]

    public void QuoteArgument_WindowsPowerShell_KeepsLiteralPortablePrefixes(
        string value)
    {

        Assert.Equal(
            value,
            CommandDisplayFormatter.QuoteArgument(
                value,
                CommandDisplayPlatform.Windows));

    }

    [Theory]

    [InlineData(CommandDisplayPlatform.MacOS)]

    [InlineData(CommandDisplayPlatform.Linux)]

    [InlineData(CommandDisplayPlatform.Windows)]

    public void QuoteArgument_SimpleToken_RemainsReadable(
        CommandDisplayPlatform platform)
    {

        Assert.Equal(
            "workspace-42",
            CommandDisplayFormatter.QuoteArgument("workspace-42", platform));

    }

    [Theory]

    [InlineData(CommandDisplayPlatform.MacOS)]

    [InlineData(CommandDisplayPlatform.Linux)]

    [InlineData(CommandDisplayPlatform.Windows)]

    public void QuoteArgument_EmptyToken_RemainsOneArgument(
        CommandDisplayPlatform platform)
    {

        Assert.Equal(
            "''",
            CommandDisplayFormatter.QuoteArgument(string.Empty, platform));

    }

    [Fact]

    public void QuoteArgumentForCurrentPlatform_UsesExplicitPlatformContract()
    {

        CommandDisplayPlatform platform = OperatingSystem.IsWindows()
            ? CommandDisplayPlatform.Windows
            : OperatingSystem.IsMacOS()
                ? CommandDisplayPlatform.MacOS
                : CommandDisplayPlatform.Linux;

        Assert.Equal(
            CommandDisplayFormatter.QuoteArgument(HostileArgument, platform),
            CommandDisplayFormatter.QuoteArgumentForCurrentPlatform(HostileArgument));

    }

}
