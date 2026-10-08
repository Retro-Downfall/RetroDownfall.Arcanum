using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Cli.Infrastructure;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// R-337: the campaign, spell and prompt import verbs read an operator-named document, so they cap it
/// like every other reader and report a path the platform rejects instead of letting the exception
/// reach the generic "unexpected CLI error" line. The read fails before any request is made, so no
/// host is needed.
/// </summary>
[Collection("GlobalConsole")]
public sealed class ImportFileReadTests
{
    [Theory]
    [InlineData("campaign", "import", "11111111-1111-1111-1111-111111111111")]
    [InlineData("spell", "import")]
    [InlineData("prompt", "import")]
    public void Import_refuses_a_file_over_the_cap_without_reading_it(params string[] verb)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-import-oversize-{Guid.NewGuid():N}.json");

        using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write))
        {
            // Sparse: the length alone puts it over the cap, so the test does not write ten megabytes.
            stream.SetLength((long)CappedInputReader.MaxAuthoredBytes + 1);
        }

        try
        {
            CliTestResult result = Run([.. verb, "--file", path]);

            Assert.Equal(1, result.ExitCode);

            Assert.Contains("byte input limit", result.Error, StringComparison.Ordinal);

            Assert.DoesNotContain("unexpected CLI error", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("campaign", "import", "11111111-1111-1111-1111-111111111111")]
    [InlineData("spell", "import")]
    [InlineData("prompt", "import")]
    public void Import_reports_a_path_the_platform_rejects_instead_of_an_unexpected_error(params string[] verb)
    {
        CliTestResult result = Run([.. verb, "--file", "bad\0name.json"]);

        Assert.Equal(1, result.ExitCode);

        Assert.Contains("Could not read file", result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain("unexpected CLI error", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static CliTestResult Run(string[] args)
    {
        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        return CliTestHarness.Run(services, args);
    }
}
