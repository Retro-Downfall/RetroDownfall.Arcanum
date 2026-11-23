using System.Diagnostics;
using System.Text.Json.Nodes;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using Xunit;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

public sealed class WorkspaceCheckAppleContainerRunnerTests
{
    private const string TrustedService = """
        {
          "status": "running", "paths": { "installRoot": "/usr/local/" },
          "client": { "version": "1.7.1" }, "server": { "version": "1.7.1", "build": "release" }
        }
        """;

    [SkippableFact]
    public void Create_uses_only_private_readonly_shares_and_closed_guest_arguments()
    {
        Skip.If(OperatingSystem.IsWindows(), "These fixtures use Unix paths and file permissions.");

        string[] args = WorkspaceCheckAppleContainerRunner.CreateArguments(
            "arcanum-check-123",
            "0123",
            "example.test/check@sha256:" + new string('a', 64),
            "example.test/init@sha256:" + new string('b', 64),
            "/private/tmp/run/source",
            "/private/tmp/run/packages",
            "src/Project Space.csproj",
            WorkspaceCheckKind.Test,
            "Release",
            "net8.0",
            4096,
            "8.0.100");

        Assert.Contains("none", args);
        Assert.Contains("--no-dns", args);
        Assert.Contains("--read-only", args);
        Assert.Contains("linux/arm64", args);
        Assert.Contains("ARCANUM_EXPECTED_SDK=8.0.100", args);
        Assert.Contains("cpu=30:30", args);
        Assert.Contains("nofile=256:256", args);
        Assert.Equal(
            [
                "type=bind,source=/private/tmp/run/source,target=/arcanum/source,readonly",
                "type=bind,source=/private/tmp/run/packages,target=/arcanum/packages,readonly",
            ],
            args.Select((arg, index) => (arg, index))
                .Where(static pair => pair.arg == "--mount")
                .Select(pair => args[pair.index + 1]));
        Assert.DoesNotContain("--publish", args);
        Assert.DoesNotContain("--publish-socket", args);
        Assert.DoesNotContain("--ssh", args);
        Assert.Equal(["test", "src/Project Space.csproj", "Release", "net8.0"], args[^4..]);
    }

    [Theory]
    [InlineData("../Escape.csproj", null)]
    [InlineData("src/Valid.csproj", "--evil")]
    [InlineData("--help.csproj", null)]
    [InlineData(@"src/Bad\Name.csproj", null)]
    public void Invalid_guest_tokens_are_refused_before_launch(string target, string? framework)
    {
        Assert.Throws<ArgumentException>(() =>
            WorkspaceCheckAppleContainerRunner.CreateArguments(
                "arcanum-check-123",
                "0123",
                "example.test/check@sha256:" + new string('a', 64),
                "example.test/init@sha256:" + new string('b', 64),
                "/private/tmp/run/source",
                "/private/tmp/run/packages",
                target,
                WorkspaceCheckKind.Build,
                null,
                framework,
                4096,
                "8.0.100"));
    }

    [Fact]
    public void Host_cli_uses_absolute_executable_and_explicit_environment()
    {
        ProcessStartInfo start = WorkspaceCheckAppleContainerRunner.CreateStartInfo(["inspect", "arcanum-check-123"]);

        Assert.Equal("/usr/local/bin/container", start.FileName);
        Assert.Equal("/", start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardInput);
        Assert.Equal(["inspect", "arcanum-check-123"], start.ArgumentList);
        Assert.DoesNotContain("SSH_AUTH_SOCK", start.Environment.Keys);
        Assert.DoesNotContain("CONTAINER_DEFAULT_PLATFORM", start.Environment.Keys);
        Assert.DoesNotContain("HTTP_PROXY", start.Environment.Keys);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_stops_and_removes_only_the_owned_vm(bool failStop)
    {
        Skip.If(OperatingSystem.IsWindows(), "These fixtures use Unix paths and file permissions.");

        DirectoryInfo stage = Directory.CreateTempSubdirectory("arcanum-vm-test-");

        string source = stage.CreateSubdirectory("source").FullName;

        string packages = stage.CreateSubdirectory("packages").FullName;

        string image = "example.test/check@sha256:" + new string('a', 64);

        string init = "example.test/init@sha256:" + new string('b', 64);

        List<string[]> commands = [];

        using CancellationTokenSource cancel = new();

        string? id = null;

        string? token = null;

        string[]? guestArguments = null;

        WorkspaceCheckAppleContainerRunner runner = new(
            () => true,
            (args, _, _, cancellationToken) =>
            {
                commands.Add(args);

                switch (args[0])
                {
                    case "system":
                        return Completed(TrustedService);
                    case "image":
                        return Completed(new JsonArray(LocalImage(image), LocalImage(init)).ToJsonString());
                    case "create":
                        id = args[2];
                        token = args[4].Split('=')[1];
                        guestArguments = args[(Array.IndexOf(args, "--entrypoint") + 3)..];
                        return Completed(id);
                    case "inspect":
                        return Completed(Inspection(id!, token!, image, source, packages, guestArguments!));
                    case "start":
                        Assert.Contains("--attach", args);
                        cancel.Cancel();
                        return Task.FromResult(new CappedChildProcessRunResult
                        {
                            Outcome = CappedChildProcessOutcome.Canceled,
                            Stdout = new("incomplete guest output", false),
                        });
                    case "stop":
                        Assert.False(cancellationToken.CanBeCanceled);
                        return Completed(id!, failStop ? 1 : 0);
                    case "launchctl":
                        return args[1] == "managername" ? Completed("Aqua") : Completed("", 113);
                    case "delete":
                        Assert.False(cancellationToken.CanBeCanceled);
                        return Completed(id!);
                    default:
                        throw new InvalidOperationException(args[0]);
                }
            });

        try
        {
            WorkspaceCheckAppleContainerRunResult result = await runner.RunAsync(
                image, init, source, packages, "Project.csproj", WorkspaceCheckKind.Test,
                "Debug", "net8.0", 4096, 8192, cancel.Token, "8.0.100");

            string journal = Path.Combine(stage.FullName, "apple-container.identity");

            Assert.Equal(!failStop, result.CleanupVerified);

            Assert.Equal(failStop, File.Exists(journal));

            Assert.Equal(failStop ? CappedChildProcessOutcome.FilesystemSandboxUnavailable
                : CappedChildProcessOutcome.Canceled, result.ProcessResult.Outcome);

            Assert.Equal(failStop ? ["system", "image", "create", "inspect", "start", "inspect", "stop"]
                : new[] { "system", "image", "create", "inspect", "start", "inspect", "stop",
                    "launchctl", "launchctl", "delete", "launchctl", "launchctl" },
                commands.Select(static args => args[0]));

            if (failStop)
            {
                Assert.Null(result.ProcessResult.Stdout.Text);
            }
            else
            {
                Assert.Equal(["delete", "--force", id!], Assert.Single(commands, command => command[0] == "delete"));
            }
        }
        finally
        {
            stage.Delete(recursive: true);
        }
    }

    [SkippableTheory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task Recovery_of_an_absent_container_requires_durable_stop_and_launchd_extinction(
        bool hasStopReceipt, bool runtimeStillRegistered, bool expected)
    {
        Skip.If(OperatingSystem.IsWindows(), "These fixtures use Unix paths and file permissions.");

        DirectoryInfo stage = Directory.CreateTempSubdirectory("arcanum-vm-recovery-");

        string identity = new('a', 32);

        string journal = Path.Combine(stage.FullName, "apple-container.identity");

        File.WriteAllText(journal, identity);

        File.WriteAllText(Path.Combine(stage.FullName, "apple-container.lock"), "");

        if (hasStopReceipt)
        {
            File.WriteAllText(Path.Combine(stage.FullName, "apple-container.stopped"), identity);
        }

        WorkspaceCheckAppleContainerRunner runner = new(
            () => true,
            (args, _, _, _) => args[0] switch
            {
                "system" => Completed(TrustedService),
                "inspect" => Completed("", 1),
                "list" => Completed("[]"),
                "launchctl" when args[1] == "managername" => Completed("Aqua"),
                "launchctl" when args[1] == "print" => Completed("", runtimeStillRegistered ? 0 : 113),
                _ => throw new InvalidOperationException(args[0]),
            });

        try
        {
            Assert.Equal(expected, await runner.RecoverAsync(stage.FullName));

            Assert.Equal(!expected, File.Exists(journal));
        }
        finally
        {
            stage.Delete(recursive: true);
        }
    }

    private static Task<CappedChildProcessRunResult> Completed(string stdout, int exitCode = 0) =>
        Task.FromResult(new CappedChildProcessRunResult
        {
            Outcome = CappedChildProcessOutcome.Completed,
            Stdout = new(stdout, false),
            ExitCode = exitCode,
        });

    private static JsonObject LocalImage(string reference) =>
        new()
        {
            ["configuration"] = new JsonObject
            {
                ["name"] = reference,
                ["descriptor"] = new JsonObject { ["digest"] = "sha256:" + new string('e', 64) },
            },
            ["variants"] = new JsonArray(new JsonObject
            {
                ["digest"] = reference.Split('@')[1],
                ["platform"] = LinuxArm64(),
            }),
        };

    private static string Inspection(string id, string token, string image, string source, string packages, string[] arguments) =>
        new JsonArray(new JsonObject
        {
            ["status"] = new JsonObject { ["state"] = "stopped" },
            ["configuration"] = new JsonObject
            {
                ["id"] = id,
                ["labels"] = new JsonObject { ["arcanum.workspace-check.run"] = token },
                ["platform"] = LinuxArm64(),
                ["image"] = new JsonObject
                {
                    ["reference"] = image,
                    ["descriptor"] = new JsonObject { ["digest"] = "sha256:" + new string('e', 64) },
                },
                ["networks"] = new JsonArray(),
                ["publishedPorts"] = new JsonArray(),
                ["publishedSockets"] = new JsonArray(),
                ["ssh"] = false,
                ["readOnly"] = true,
                ["virtualization"] = false,
                ["runtimeHandler"] = "container-runtime-linux",
                ["capAdd"] = new JsonArray(),
                ["capDrop"] = new JsonArray("ALL"),
                ["resources"] = new JsonObject { ["cpus"] = 2, ["memoryInBytes"] = 4L * 1024 * 1024 * 1024 },
                ["initProcess"] = new JsonObject
                {
                    ["executable"] = "/bin/sh",
                    ["workingDirectory"] = "/tmp",
                    ["arguments"] = new JsonArray(arguments.Select(static argument => (JsonNode?)JsonValue.Create(argument)).ToArray()),
                    ["user"] = new JsonObject { ["raw"] = new JsonObject { ["userString"] = "65532:65532" } },
                    ["environment"] = new JsonArray("ARCANUM_EXPECTED_SDK=8.0.100"),
                    ["rlimits"] = new JsonArray(
                        new JsonObject { ["limit"] = "RLIMIT_CPU", ["hard"] = 30, ["soft"] = 30 },
                        new JsonObject { ["limit"] = "RLIMIT_NOFILE", ["hard"] = 256, ["soft"] = 256 }),
                },
                ["mounts"] = new JsonArray(
                    Mount("tmpfs", "tmpfs", "/tmp", []),
                    Mount("virtiofs", source, "/arcanum/source", ["ro"]),
                    Mount("virtiofs", packages, "/arcanum/packages", ["ro"])),
            },
        }).ToJsonString();

    private static JsonObject Mount(string type, string source, string destination, string[] options) =>
        new()
        {
            ["type"] = new JsonObject { [type] = new JsonObject() },
            ["source"] = source,
            ["destination"] = destination,
            ["options"] = new JsonArray(options.Select(static option => (JsonNode?)JsonValue.Create(option)).ToArray()),
        };

    private static JsonObject LinuxArm64() => new() { ["os"] = "linux", ["architecture"] = "arm64" };
}
