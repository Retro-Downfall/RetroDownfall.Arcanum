using System.Diagnostics;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>
/// Gives a published-process qualification one private temporary root while keeping its synthetic
/// home beneath the credential policy's <c>arcanum-tests</c> boundary after the child process
/// evaluates <see cref="Path.GetTempPath"/> from its rebased environment.
/// </summary>
internal sealed class PublishedTestProcessIsolation
{
    private const string TestRootDirectoryName = "arcanum-tests";

    private PublishedTestProcessIsolation(
        string processTemporaryDirectory,
        string testHome)
    {
        ProcessTemporaryDirectory = processTemporaryDirectory;

        TestHome = testHome;
    }

    internal string ProcessTemporaryDirectory { get; }

    internal string TestHome { get; }

    internal static PublishedTestProcessIsolation Create(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        if (purpose.Any(static character =>
            !char.IsAsciiLetterOrDigit(character) && character is not '-'))
        {
            throw new ArgumentException(
                "A published-test isolation purpose may contain only ASCII letters, digits, and hyphens.",
                nameof(purpose));
        }

        string processTemporaryDirectory = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"arcanum-{purpose}-{Guid.NewGuid():N}"));

        string testHome = Path.Combine(
            processTemporaryDirectory,
            TestRootDirectoryName,
            "home");

        return new PublishedTestProcessIsolation(processTemporaryDirectory, testHome);
    }

    internal void Prepare()
    {
        Directory.CreateDirectory(TestHome);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                ProcessTemporaryDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    internal void ApplyTemporaryEnvironment(ProcessStartInfo start)
    {
        ArgumentNullException.ThrowIfNull(start);

        if (!Directory.Exists(TestHome))
        {
            throw new InvalidOperationException(
                "Published-test isolation must be prepared before launching a child process.");
        }

        start.Environment["TMPDIR"] = ProcessTemporaryDirectory;

        start.Environment["TMP"] = ProcessTemporaryDirectory;

        start.Environment["TEMP"] = ProcessTemporaryDirectory;
    }

    internal void Delete() =>
        LocalOllamaQualificationGuards.DeleteDirectoryWithRetries(ProcessTemporaryDirectory);
}
