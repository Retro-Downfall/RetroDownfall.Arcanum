using RetroDownfall.Arcanum.Infrastructure.Familiars;

namespace RetroDownfall.Arcanum.Tests.Familiars;

/// <summary>
/// Where a Familiar runs is a security decision. The type's own remarks rule out the shared temp
/// root — on Linux it is mode 1777, so any local account can plant the <c>AGENTS.md</c>,
/// <c>.claude/settings.json</c>, or <c>output-schema.json</c> that steers or is written by the turn.
/// These facts pin that the fallback path honours that, rather than quietly running there.
/// </summary>
[Collection("ProcessEnvironment")]
public sealed class FamiliarWorkingDirectoryTests
{

    [Fact]
    public void A_created_directory_is_private_and_not_the_shared_temp_root()
    {

        using FamiliarWorkingDirectory directory = FamiliarWorkingDirectory.Create();

        Assert.True(Directory.Exists(directory.Path));

        Assert.NotEqual(
            Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
            Path.TrimEndingDirectorySeparator(directory.Path));

    }

    [SkippableFact]
    public void Creation_fails_the_turn_rather_than_falling_back_to_the_shared_temp_root()
    {

        Skip.If(OperatingSystem.IsWindows(), "Unix permission bits are what make the temp root unwritable here.");

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        string unwritable = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-familiar-unwritable-{Guid.NewGuid():N}");

        Directory.CreateDirectory(unwritable);

        string? originalTmpDir = global::System.Environment.GetEnvironmentVariable("TMPDIR");

        try
        {

            File.SetUnixFileMode(unwritable, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            global::System.Environment.SetEnvironmentVariable("TMPDIR", unwritable);

            Skip.If(
                Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                    != Path.TrimEndingDirectorySeparator(unwritable),
                "This runtime does not resolve the temp root from TMPDIR.");

            _ = Assert.Throws<IOException>(FamiliarWorkingDirectory.Create);

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable("TMPDIR", originalTmpDir);

            File.SetUnixFileMode(
                unwritable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            Directory.Delete(unwritable, recursive: true);

        }

    }

}
