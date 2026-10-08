using System.Text;

using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The destination handling every file-writing verb shares: what it settles before the work, and what the
/// replacement of an existing file keeps.
/// </summary>
[Collection("GlobalConsole")]
public sealed class CliOutputFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"arcanum-output-file-{Guid.NewGuid():N}");

    public CliOutputFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Planning_an_export_with_no_output_goes_to_stdout_and_asks_nothing()
    {
        RecordingPrompt prompt = new(answer: false);

        ExportDestination destination = await CliOutputFile.PlanExportAsync(
            output: null,
            prompt,
            CreatePalette(),
            CancellationToken.None);

        Assert.True(destination.Proceed);

        Assert.Null(destination.FullPath);

        Assert.Empty(prompt.Questions);
    }

    [Fact]
    public async Task Planning_an_export_over_an_existing_file_asks_once_and_a_decline_stops_the_verb()
    {
        string path = Path.Combine(_directory, "export.json");

        await File.WriteAllTextAsync(path, "original");

        RecordingPrompt prompt = new(answer: false);

        using ConsoleCapture capture = new();

        ExportDestination destination = await CliOutputFile.PlanExportAsync(
            path,
            prompt,
            CreatePalette(),
            CancellationToken.None);

        Assert.False(destination.Proceed);

        Assert.Equal(0, destination.ExitCode);

        Assert.Equal("original", await File.ReadAllTextAsync(path));

        Assert.Contains(path, Assert.Single(prompt.Questions), StringComparison.Ordinal);

        Assert.Contains("Export cancelled", capture.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Planning_an_export_to_a_missing_directory_fails_before_the_prompt()
    {
        string path = Path.Combine(_directory, "missing", "export.json");

        RecordingPrompt prompt = new(answer: true);

        using ConsoleCapture capture = new();

        ExportDestination destination = await CliOutputFile.PlanExportAsync(
            path,
            prompt,
            CreatePalette(),
            CancellationToken.None);

        Assert.False(destination.Proceed);

        Assert.Equal(1, destination.ExitCode);

        Assert.Empty(prompt.Questions);

        // The console wraps a long line, so compare with every run of whitespace read as one space.
        string reported = System.Text.RegularExpressions.Regex.Replace(capture.Error, @"\s+", " ");

        Assert.Contains("Could not write", reported, StringComparison.Ordinal);

        Assert.Contains("does not exist", reported, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writing_replaces_the_destination_and_leaves_no_temporary_sibling()
    {
        string path = Path.Combine(_directory, "answer.md");

        await File.WriteAllTextAsync(path, "original");

        await CliOutputFile.WriteAllTextAsync(path, "replacement", null, CancellationToken.None);

        Assert.Equal("replacement", await File.ReadAllTextAsync(path));

        Assert.Equal(
            ["answer.md"],
            Directory.GetFileSystemEntries(_directory).Select(Path.GetFileName));
    }

    /// <summary>
    /// R-327: an export the operator made owner-only stays owner-only when it is replaced. A replacement
    /// made through a sibling is a new file, which comes back with the default permissions unless the
    /// destination's are carried over.
    /// </summary>
    [SkippableFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Replacing_an_existing_file_keeps_its_permission_bits()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix permission bits.");

        string path = Path.Combine(_directory, "private.json");

        await File.WriteAllTextAsync(path, "original");

        UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        File.SetUnixFileMode(path, ownerOnly);

        await CliOutputFile.WriteAllTextAsync(path, "replacement", null, CancellationToken.None);

        Assert.Equal("replacement", await File.ReadAllTextAsync(path));

        Assert.Equal(ownerOnly, File.GetUnixFileMode(path));
    }

    /// <summary>
    /// R-327: a symbolic link names its target, so the write goes to the target and the link stays a link.
    /// </summary>
    [SkippableFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Writing_through_a_symbolic_link_replaces_its_target_and_keeps_the_link()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        string target = Path.Combine(_directory, "target.md");

        string link = Path.Combine(_directory, "link.md");

        await File.WriteAllTextAsync(target, "original");

        File.CreateSymbolicLink(link, target);

        await CliOutputFile.WriteAllTextAsync(link, "replacement", null, CancellationToken.None);

        Assert.NotNull(new FileInfo(link).LinkTarget);

        Assert.Equal("replacement", await File.ReadAllTextAsync(target));

        Assert.Equal("replacement", await File.ReadAllTextAsync(link));

        Assert.Equal(
            ["link.md", "target.md"],
            Directory.GetFileSystemEntries(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// R-327: when the replacement fails, the failure that is reported is the write's own. A temporary
    /// sibling that cannot be removed afterwards must not replace it.
    /// </summary>
    [Fact]
    public async Task A_failed_removal_of_the_temporary_sibling_does_not_replace_the_write_failure()
    {
        // A directory where the file should go makes the final move fail, with the temporary file
        // already written.
        string destination = Path.Combine(_directory, "is-a-directory");

        Directory.CreateDirectory(destination);

        List<string> removed = [];

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(
            () => CliOutputFile.WriteAllTextAsync(
                destination,
                "content",
                Encoding.UTF8,
                path =>
                {
                    removed.Add(path);
                    throw new IOException("the-removal-failure-marker");
                },
                CancellationToken.None));

        Assert.True(
            failure is IOException or UnauthorizedAccessException,
            $"Unexpected failure type {failure.GetType()}.");

        Assert.DoesNotContain("the-removal-failure-marker", failure.Message, StringComparison.Ordinal);

        // The cleanup this is about must actually have run, against the write's own temporary sibling.
        string attempted = Assert.Single(removed);
        Assert.Equal(Path.GetFileName(_directory), Path.GetFileName(Path.GetDirectoryName(attempted)));
        Assert.Matches(@"^\.is-a-directory\.[0-9a-f]{32}\.tmp$", Path.GetFileName(attempted));
    }

    /// <summary>
    /// R-327 on Windows: <c>File.Replace</c> keeps the replaced file's attributes, so a file the operator
    /// marked hidden stays hidden. Not run on the macOS development host; it runs on the Windows lane.
    /// </summary>
    [SkippableFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Replacing_an_existing_file_keeps_its_attributes_on_Windows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "File.Replace attribute preservation is Windows behaviour.");

        string path = Path.Combine(_directory, "hidden.json");

        await File.WriteAllTextAsync(path, "original");

        File.SetAttributes(path, FileAttributes.Hidden);

        await CliOutputFile.WriteAllTextAsync(path, "replacement", null, CancellationToken.None);

        Assert.Equal("replacement", await File.ReadAllTextAsync(path));

        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
    }

    private static ConfiguredThemePalette CreatePalette() =>
        new(new ThemeSemanticColors(), new ThemeSemanticColors());

    private sealed class RecordingPrompt(bool answer) : IConfirmationPrompt
    {
        public List<string> Questions { get; } = [];

        public Task<bool> PromptForConfirmationAsync(
            string question,
            CancellationToken cancellationToken)
        {
            Questions.Add(question);

            return Task.FromResult(answer);
        }
    }

    private sealed class ConsoleCapture : IDisposable
    {
        private readonly TextWriter _priorError = Console.Error;

        private readonly StringWriter _error = new();

        public ConsoleCapture() => Console.SetError(_error);

        public string Error => _error.ToString();

        public void Dispose() => Console.SetError(_priorError);
    }
}
