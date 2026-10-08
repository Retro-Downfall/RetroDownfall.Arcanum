using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Cli.Commands.Tower;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// R-337: every reader of an operator-named file or piped text is capped, so a path that is not what
/// the operator thought it was (a log, a device, a database) is refused with a diagnostic that names
/// the limit instead of being buffered whole.
/// </summary>
[Collection("GlobalConsole")]
public sealed class CliArgReaderTests
{
    [Fact]
    public void TryReadInlineOrFile_rejects_a_file_over_the_cap()
    {
        string path = WriteFile(new string('x', 33));

        try
        {
            bool read = CliArgReader.TryReadInlineOrFile(
                $"@{path}",
                maxBytes: 32,
                out string result,
                out string? error);

            Assert.False(read);

            Assert.Equal(string.Empty, result);

            Assert.NotNull(error);

            Assert.Contains("32-byte", error, StringComparison.Ordinal);

            Assert.Contains(path, error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadInlineOrFile_reads_a_file_exactly_at_the_cap()
    {
        string path = WriteFile(new string('x', 32));

        try
        {
            bool read = CliArgReader.TryReadInlineOrFile(
                $"@{path}",
                maxBytes: 32,
                out string result,
                out string? error);

            Assert.True(read, error);

            Assert.Equal(new string('x', 32), result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadInlineOrFile_default_cap_matches_the_hosts_request_body_default()
    {
        Assert.Equal(
            RetroDownfall.Arcanum.Core.Configuration.ArcanumRuntimeDefaults.HostMaxRequestBodyBytes,
            CappedInputReader.MaxAuthoredBytes);
    }

    [Fact]
    public async Task AuthoredContentReader_rejects_a_file_over_the_cap_before_sending_anything()
    {
        string path = WriteFile(new string('x', 33));

        try
        {
            RetroDownfall.Arcanum.Core.Primitives.Result<string> read = await AuthoredContentReader.ReadAsync(
                path,
                "Covenant",
                emptyContentRemedy: null,
                maxBytes: 32,
                CancellationToken.None);

            Assert.True(read.IsFailure);

            Assert.Equal(
                RetroDownfall.Arcanum.Core.Primitives.ErrorCodes.Validation.BodyTooLarge,
                read.Error.Code);

            Assert.Contains("32-byte", read.Error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AuthoredContentReader_rejects_piped_input_over_the_cap()
    {
        TextReader original = Console.In;

        Console.SetIn(new StringReader(new string('y', 33)));

        try
        {
            RetroDownfall.Arcanum.Core.Primitives.Result<string> read = await AuthoredContentReader.ReadAsync(
                "-",
                "Covenant",
                emptyContentRemedy: null,
                maxBytes: 32,
                CancellationToken.None);

            Assert.True(read.IsFailure);

            Assert.Equal(
                RetroDownfall.Arcanum.Core.Primitives.ErrorCodes.Validation.BodyTooLarge,
                read.Error.Code);
        }
        finally
        {
            Console.SetIn(original);
        }
    }

    [Fact]
    public async Task AuthoredContentReader_still_reads_a_small_file()
    {
        string path = WriteFile("a short preference");

        try
        {
            RetroDownfall.Arcanum.Core.Primitives.Result<string> read = await AuthoredContentReader.ReadAsync(
                path,
                "Covenant",
                emptyContentRemedy: null,
                CancellationToken.None);

            Assert.True(read.IsSuccess, read.Error.Message);

            Assert.Equal("a short preference", read.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteFile(string content)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-capped-{Guid.NewGuid():N}.txt");

        File.WriteAllText(path, content);

        return path;
    }
}
