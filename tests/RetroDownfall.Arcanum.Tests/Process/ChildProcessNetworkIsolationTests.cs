using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// <see cref="ChildProcessSandboxRequest.DenyNetwork"/> on macOS, against real sandboxed children: a
/// connection attempt to a listener this test owns is refused under the denial and reaches it without,
/// and a .NET child (managed apphost, and the published Native AOT binary when one is named) still
/// starts with no network and no sockets.
/// </summary>
[Collection("ProcessEnvironment")]
public sealed class ChildProcessNetworkIsolationTests
{
    private const string PublishedExecutableVariable = "ARCANUM_PUBLISHED_EXECUTABLE";

    private const string MalformedBrokerArgumentsMessage = "sandbox-exec: malformed arguments.";

    private const int MalformedBrokerArgumentsExitCode = 64;

    private const string ConnectScript =
        "use IO::Socket::INET; "
        + "my $s = IO::Socket::INET->new(PeerAddr => '127.0.0.1', PeerPort => $ARGV[0], Proto => 'tcp', Timeout => 5); "
        + "if ($s) { print \"connected\\n\"; close $s; exit 0 } "
        + "print \"refused: $!\\n\"; exit 3";

    [SkippableFact]
    public async Task A_denied_child_cannot_reach_a_local_listener()
    {
        SkipUnlessSeatbeltApplies();

        using TcpListener listener = new(IPAddress.Loopback, 0);

        listener.Start();

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        CappedChildProcessRunResult result = await RunConnectProbeAsync(port, denyNetwork: true);

        Assert.True(result.Outcome == CappedChildProcessOutcome.Completed, Describe(result));

        Assert.True(result.ExitCode == 3, Describe(result));

        Assert.StartsWith("refused:", result.Stdout.Text, StringComparison.Ordinal);

        Assert.False(listener.Pending(), "A connection reached the listener from a child whose network was denied.");
    }

    [SkippableFact]
    public async Task A_child_without_the_denial_still_reaches_a_local_listener()
    {
        SkipUnlessSeatbeltApplies();

        // The control: the same probe through the same jail connects when the denial is off, so the
        // refusal above is the denial and not a probe that cannot connect anywhere.
        using TcpListener listener = new(IPAddress.Loopback, 0);

        listener.Start();

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        CappedChildProcessRunResult result = await RunConnectProbeAsync(port, denyNetwork: false);

        Assert.True(result.Outcome == CappedChildProcessOutcome.Completed, Describe(result));

        Assert.True(result.ExitCode == 0, Describe(result));

        Assert.Equal("connected", result.Stdout.Text.Trim());

        Assert.True(listener.Pending(), "The child reported a connection the listener never received.");
    }

    [SkippableFact]
    public async Task A_managed_dotnet_child_starts_with_no_network_and_no_sockets()
    {
        SkipUnlessSeatbeltApplies();

        string apphost = Path.Combine(AppContext.BaseDirectory, "RetroDownfall.Arcanum.Cli");

        Skip.IfNot(File.Exists(apphost), "The Cli apphost is not in the test output.");

        // The apphost finds the shared runtime through DOTNET_ROOT, which a jailed child must also be
        // allowed to read and map.
        string dotnetRoot = Path.GetFullPath(
            Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));

        ProcessStartInfo psi = CreateMalformedBrokerStartInfo(apphost);

        psi.Environment.Clear();

        psi.Environment["DOTNET_ROOT"] = dotnetRoot;

        CappedChildProcessRunResult result = await RunDeniedDotNetChildAsync(
            psi,
            ChildProcessEnvironmentProfile.WorkspaceCheck,
            AppContext.BaseDirectory,
            dotnetRoot);

        AssertBrokerEntryRan(result);
    }

    [SkippableFact]
    public async Task The_published_native_aot_binary_starts_with_no_network_no_sockets_and_no_environment()
    {
        SkipUnlessSeatbeltApplies();

        string executable = global::System.Environment.GetEnvironmentVariable(PublishedExecutableVariable) ?? string.Empty;

        Skip.If(
            string.IsNullOrWhiteSpace(executable),
            $"Set {PublishedExecutableVariable} to a published arcanum binary to run the Native AOT probe.");

        Assert.True(File.Exists(executable), $"Published executable does not exist: {executable}");

        CappedChildProcessRunResult result = await RunDeniedDotNetChildAsync(
            CreateMalformedBrokerStartInfo(executable),
            ChildProcessEnvironmentProfile.DocumentWorker,
            Path.GetDirectoryName(Path.GetFullPath(executable))!);

        AssertBrokerEntryRan(result);
    }

    private static void SkipUnlessSeatbeltApplies() =>
        Skip.IfNot(
            MacOsSandboxProbe.IsSandboxExecRunnable(),
            "Needs a macOS host where sandbox-exec can apply a profile.");

    private static async Task<CappedChildProcessRunResult> RunConnectProbeAsync(
        int port,
        bool denyNetwork)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "/usr/bin/perl",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-e", ConnectScript, port.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        };

        ChildProcessSandboxRequest request = new()
        {
            ReadWriteRoots = [],
            ReadExecuteRoots = ["/usr/bin"],
            RequireAppliedFilesystemJail = true,
            DenyNetwork = denyNetwork,
            ToolName = "network-denial-probe",
        };

        return await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            64 * 1024,
            TimeSpan.FromSeconds(60),
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None,
            request,
            NullLogger.Instance);
    }

    /// <summary>
    /// The broker entry is the first statement of the Cli's <c>Main</c>: a malformed broker argv makes the
    /// started runtime print one line and exit 64, before configuration or anything else is touched. That
    /// is enough to prove the runtime started and ran managed code under the profile.
    /// </summary>
    private static ProcessStartInfo CreateMalformedBrokerStartInfo(string executable) => new()
    {
        FileName = executable,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        ArgumentList = { ChildProcessFilesystemJail.HelperArg },
    };

    private static async Task<CappedChildProcessRunResult> RunDeniedDotNetChildAsync(
        ProcessStartInfo psi,
        ChildProcessEnvironmentProfile environmentProfile,
        params string[] readExecuteRoots)
    {
        ChildProcessSandboxRequest request = new()
        {
            ReadWriteRoots = [],
            ReadExecuteRoots = readExecuteRoots,
            RequireAppliedFilesystemJail = true,
            DenyNetwork = true,
            ToolName = "network-denial-probe",
        };

        return await CappedChildProcessRunner.RunAsync(
            psi,
            environmentProfile,
            64 * 1024,
            TimeSpan.FromSeconds(60),
            new ResourceLimits(
                MaxProcessMemoryMb: 768,
                MaxProcessCount: 1,
                MaxFileWriteMb: 0,
                ProcessTimeoutSeconds: 10,
                MaxCpuSeconds: 15,
                MaxMemoryMb: 768,
                MaxFileDescriptors: 64),
            new ProcessResourceLimiter(),
            CancellationToken.None,
            request,
            NullLogger.Instance);
    }

    private static void AssertBrokerEntryRan(CappedChildProcessRunResult result)
    {
        Assert.True(result.Outcome == CappedChildProcessOutcome.Completed, Describe(result));

        Assert.True(result.ExitCode == MalformedBrokerArgumentsExitCode, Describe(result));

        Assert.Equal(MalformedBrokerArgumentsMessage, result.Stderr.Text.Trim());
    }

    private static string Describe(CappedChildProcessRunResult result) =>
        $"Outcome {result.Outcome}; exit {result.ExitCode}; stdout: {result.Stdout.Text}; stderr: {result.Stderr.Text}; "
        + $"sandbox: {result.FilesystemSandboxDenialMessage}; limits: {result.ResourceLimitApplyError}";
}
