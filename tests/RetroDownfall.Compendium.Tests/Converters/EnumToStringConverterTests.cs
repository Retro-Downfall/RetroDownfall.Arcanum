using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Compendium.Ux.Converters;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Converters;

public sealed class EnumToStringConverterTests
{

    [Theory]

    [InlineData("OpenAICompatible", "OpenAI Compatible")]

    [InlineData("SystemDefault", "System Default")]

    [InlineData("LlamaCppServer", "Llama Cpp Server")]

    [InlineData("Light", "Light")]

    [InlineData("Dark", "Dark")]

    [InlineData("Information", "Information")]

    [InlineData("", "")]

    public void Convert_inserts_spaces_before_capitals(string input, string expected)
    {

        string actual = EnumToStringConverter.ToHumanReadable(input);

        Assert.Equal(expected, actual);

    }

    [Fact]

    public void Convert_returns_human_readable_for_AiProviderKind_members()
    {

        EnumToStringConverter converter = new();

        Assert.Equal("OpenAI Compatible", (string)converter.Convert(AiProviderKind.OpenAICompatible, null, null, null));

        Assert.Equal("Llama Cpp Server", (string)converter.Convert(AiProviderKind.LlamaCppServer, null, null, null));

    }

    [Fact]

    public void Convert_returns_empty_for_null()
    {

        EnumToStringConverter converter = new();

        Assert.Equal(string.Empty, (string)converter.Convert(null, null, null, null));

    }

    [Fact]

    public void ConvertBack_returns_DoNothing()
    {

        EnumToStringConverter converter = new();

        Assert.Equal(Avalonia.AvaloniaProperty.UnsetValue, converter.ConvertBack("OpenAI Compatible", typeof(object), null!, null!));

    }

}
