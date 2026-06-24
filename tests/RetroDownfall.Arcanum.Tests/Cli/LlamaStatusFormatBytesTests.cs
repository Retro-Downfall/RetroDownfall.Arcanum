using RetroDownfall.Arcanum.Cli.Commands.Llama;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class LlamaStatusFormatBytesTests
{

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    public void FormatBytes_returns_bytes_for_values_under_one_kibibyte(long bytes, string expected)
    {

        string actual = LlamaStatusCommand.FormatBytes(bytes);

        Assert.Equal(expected, actual);

    }

    [Theory]
    [InlineData(1024, "1.0 KiB")]
    [InlineData(1536, "1.5 KiB")]
    [InlineData(1024L * 1024 - 1, "1024.0 KiB")]
    public void FormatBytes_returns_kibibytes_for_values_under_one_mebibyte(long bytes, string expected)
    {

        string actual = LlamaStatusCommand.FormatBytes(bytes);

        Assert.Equal(expected, actual);

    }

    [Theory]
    [InlineData(1024L * 1024, "1.0 MiB")]
    [InlineData(5L * 1024 * 1024, "5.0 MiB")]
    public void FormatBytes_returns_mebibytes_for_values_under_one_gibibyte(long bytes, string expected)
    {

        string actual = LlamaStatusCommand.FormatBytes(bytes);

        Assert.Equal(expected, actual);

    }

    [Fact]
    public void FormatBytes_returns_gibibytes_for_large_values()
    {

        long bytes = 3L * 1024 * 1024 * 1024;

        string actual = LlamaStatusCommand.FormatBytes(bytes);

        Assert.Equal("3.0 GiB", actual);

    }

}
