using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// The environment policy each child-process profile applies before anything else touches the start.
/// </summary>
[Collection("ChildProcess")]
public sealed class ChildProcessEnvironmentScrubberTests
{
    /// <summary>
    /// What the macOS launch chain itself puts back: the supervisor and prelude shells export their own
    /// <c>PWD</c>, <c>SHLVL</c> and <c>_</c>, and the jail points the temp variables at the run's private
    /// directory. None of them comes from the host.
    /// </summary>
    private static readonly HashSet<string> LaunchChainVariables =
        new(StringComparer.Ordinal) { "PWD", "SHLVL", "_", "TMPDIR", "TMP", "TEMP" };

    [SkippableFact]
    public async Task A_jailed_document_worker_sees_only_its_private_temp_directory()
    {
        Skip.IfNot(
            MacOsSandboxProbe.IsSandboxExecRunnable(),
            "Needs a macOS host where sandbox-exec can apply a profile.");

        ProcessStartInfo psi = new()
        {
            FileName = "/usr/bin/env",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        psi.Environment["ARCANUM_DOCUMENT_WORKER_SENTINEL"] = "host-secret";

        psi.Environment["OPERATOR_NAMED_SECRET"] = "host-secret";

        ChildProcessSandboxRequest request = new()
        {
            ReadWriteRoots = [],
            ReadExecuteRoots = ["/usr/bin"],
            RequireAppliedFilesystemJail = true,
            DenyNetwork = true,
            ToolName = "document-worker-environment-probe",
        };

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.DocumentWorker,
            64 * 1024,
            TimeSpan.FromSeconds(60),
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None,
            request,
            NullLogger.Instance);

        Assert.True(
            result.Outcome == CappedChildProcessOutcome.Completed && result.ExitCode == 0,
            $"Outcome {result.Outcome}; exit {result.ExitCode}; stderr: {result.Stderr.Text}");

        Dictionary<string, string> seen = ParseEnvironment(result.Stdout.Text);

        Assert.DoesNotContain("host-secret", result.Stdout.Text, StringComparison.Ordinal);

        Assert.All(seen.Keys, name => Assert.Contains(name, LaunchChainVariables));

        string temp = Assert.Contains("TMPDIR", seen);

        Assert.StartsWith("/private/tmp/a-", temp, StringComparison.Ordinal);

        Assert.Equal(temp, seen["TMP"]);

        Assert.Equal(temp, seen["TEMP"]);
    }

    private static Dictionary<string, string> ParseEnvironment(string output)
    {
        Dictionary<string, string> variables = new(StringComparer.Ordinal);

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=', StringComparison.Ordinal);

            Assert.True(separator > 0, $"Unexpected env output line: {line}");

            variables[line[..separator]] = line[(separator + 1)..];
        }

        return variables;
    }

    [Fact]
    public void DocumentWorker_profile_clears_the_entire_environment()
    {
        // A document worker parses hostile bytes and needs nothing from the host: no PATH, no HOME, no
        // provider key under any name. Scrubbing by prefix or by a declared-name list would leave every
        // name nobody thought to list, so the profile keeps nothing at all.
        ProcessStartInfo startInfo = new()
        {
            FileName = "/usr/bin/true",
        };

        startInfo.Environment["PATH"] = "/usr/bin:/bin";

        startInfo.Environment["HOME"] = "/Users/operator";

        startInfo.Environment["ARCANUM_PROVIDER_KEY"] = "arcanum-secret";

        startInfo.Environment["MY_OPENAI_KEY"] = "operator-named-secret";

        startInfo.Environment["DYLD_INSERT_LIBRARIES"] = "/tmp/hijack.dylib";

        ChildProcessEnvironmentScrubber.ApplyProfile(
            startInfo,
            ChildProcessEnvironmentProfile.DocumentWorker);

        // Windows cannot start a process without its system directory, so there the profile reads those two back from
        // the host; nothing that was on the start survives on either OS.
        Assert.Subset(
            OperatingSystem.IsWindows() ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SystemRoot", "windir" } : [],
            startInfo.Environment.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>A host environment holding everything a document worker must not see, and what Windows needs.</summary>
    private static readonly Dictionary<string, string> HostEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = @"C:\Windows",
        ["windir"] = @"C:\Windows",
        ["PATH"] = @"C:\Windows\system32;C:\Tools",
        ["USERPROFILE"] = @"C:\Users\operator",
        ["TEMP"] = @"C:\Users\operator\AppData\Local\Temp",
        ["TMP"] = @"C:\Users\operator\AppData\Local\Temp",
        ["ARCANUM_PROVIDER_KEY"] = "arcanum-secret",
        ["MY_OPENAI_KEY"] = "operator-named-secret",
        ["COMSPEC"] = @"C:\Windows\system32\cmd.exe",
    };

    [Fact]
    public void DocumentWorker_environment_on_Windows_is_only_the_system_directory_the_process_start_needs()
    {
        // Windows process start needs SystemRoot (workspace_check keeps it for the same reason), and windir names the
        // same directory. TMP and TEMP are not taken from the host: the jail applied after this points them at the run's
        // private directory. Nothing already on the start survives.
        Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ARCANUM_PROVIDER_KEY"] = "arcanum-secret",
            ["PATH"] = "/usr/bin",
        };

        ChildProcessEnvironmentScrubber.ApplyDocumentWorkerEnvironment(environment, windows: true, ReadHost);

        Assert.Equal(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["SystemRoot"] = @"C:\Windows", ["windir"] = @"C:\Windows" },
            environment);
    }

    [Fact]
    public void DocumentWorker_environment_on_Windows_leaves_out_a_system_directory_the_host_does_not_have()
    {
        Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase);

        ChildProcessEnvironmentScrubber.ApplyDocumentWorkerEnvironment(
            environment,
            windows: true,
            name => name.Equals("SystemRoot", StringComparison.OrdinalIgnoreCase) ? @"C:\Windows" : null);

        Assert.Equal(["SystemRoot"], environment.Keys);
    }

    [Fact]
    public void DocumentWorker_environment_off_Windows_is_empty()
    {
        // macOS needs nothing from the host; the jail adds TMPDIR, TMP and TEMP afterwards.
        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            ["HOME"] = "/Users/operator",
        };

        ChildProcessEnvironmentScrubber.ApplyDocumentWorkerEnvironment(environment, windows: false, ReadHost);

        Assert.Empty(environment);
    }

    private static string? ReadHost(string name) => HostEnvironment.TryGetValue(name, out string? value) ? value : null;

    [Fact]
    public void Every_profile_is_handled_explicitly()
    {
        // The switch once had no default, so a profile added without a case applied no policy at all and
        // its child silently inherited every host variable. Each defined profile must have a case of its
        // own; an undefined value is refused rather than treated as "scrub nothing".
        foreach (ChildProcessEnvironmentProfile profile in Enum.GetValues<ChildProcessEnvironmentProfile>())
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "/usr/bin/true",
            };

            Exception? failure = Record.Exception(() => ChildProcessEnvironmentScrubber.ApplyProfile(
                startInfo,
                profile));

            Assert.True(failure is null, $"Profile {profile} has no explicit case: {failure?.Message}");
        }

        ProcessStartInfo undefined = new()
        {
            FileName = "/usr/bin/true",
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => ChildProcessEnvironmentScrubber.ApplyProfile(
            undefined,
            (ChildProcessEnvironmentProfile)int.MaxValue));
    }
}
