using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using Result = RetroDownfall.Arcanum.Core.Primitives.Result;
using RetroDownfall.Arcanum.Infrastructure.Familiars;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

[Collection("WorkspacePathPolicy")]
public sealed class WorkspaceMutationFenceProcessTests : IAsyncLifetime
{
    private readonly IDisposable _history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public async Task DisposeAsync()
    {
        await _workspace.DisposeAsync();

        _history.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Installed_fence_refuses_uncontained_process_before_start(int profileIndex)
    {
        ChildProcessEnvironmentProfile[] profiles = [ChildProcessEnvironmentProfile.ToolExec,
            ChildProcessEnvironmentProfile.SpellScript, ChildProcessEnvironmentProfile.WorkspaceCheck];

        using IDisposable installed = WorkspaceMutationFence.Install(new ProtectedFence());

        string marker = Path.Combine(_workspace.Root, "started");

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(MarkerProcess(marker), profiles[profileIndex], 4096,
            TimeSpan.FromSeconds(10), null, null, CancellationToken.None);

        Assert.Equal(ErrorCodes.Guild.ToolBoundaryUnavailable, result.PreStartValidationCode);

        Assert.False(File.Exists(marker));
    }

    [SkippableFact]
    public async Task Applied_no_write_no_network_jail_remains_eligible_under_installed_fence()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "This live sandbox qualification uses the macOS adapter.");

        using IDisposable installed = WorkspaceMutationFence.Install(new ProtectedFence());

        string marker = Path.Combine(_workspace.Root, "forbidden-write");

        ProcessStartInfo start = MarkerProcess(marker);

        start.ArgumentList[1] = "printf forbidden > \"$1\"; printf readonly";

        ChildProcessSandboxRequest sandbox = new()
        {
            ReadWriteRoots = [],
            ReadOnlyRoots = [_workspace.Root],
            ReadExecuteRoots = ["/bin", "/usr", "/System"],
            RequireAppliedFilesystemJail = true,
            DenyFileWrites = true,
            DenyNetwork = true,
        };

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(start, ChildProcessEnvironmentProfile.ToolExec,
            4096, TimeSpan.FromSeconds(10), null, null, CancellationToken.None, sandbox);

        Skip.If(result.Outcome == CappedChildProcessOutcome.FilesystemSandboxUnavailable, result.FilesystemSandboxDenialMessage);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.Equal("readonly", result.Stdout.Text);

        Assert.False(File.Exists(marker));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Familiar_direct_entrypoints_refuse_before_start(bool stream)
    {
        using IDisposable installed = WorkspaceMutationFence.Install(new ProtectedFence());

        string marker = Path.Combine(_workspace.Root, "familiar-started");

        ProcessStartInfo start = MarkerProcess(marker);

        FamiliarProcessRequest request = new()
        {
            FileName = start.FileName,
            Arguments = start.ArgumentList.ToArray(),
            WorkingDirectory = Path.GetTempPath(),
        };

        FamiliarProcessRunner runner = new();

        if (stream)
        {
            FamiliarProcessException error = await Assert.ThrowsAsync<FamiliarProcessException>(async () =>
            {
                await foreach (string line in runner.RunLinesAsync(request, CancellationToken.None)) { }
            });

            Assert.Contains(ErrorCodes.Guild.ToolBoundaryUnavailable, error.Message);
        }
        else
        {
            FamiliarProcessOutput result = await runner.RunToCompletionAsync(request, CancellationToken.None);

            Assert.Contains(ErrorCodes.Guild.ToolBoundaryUnavailable, result.StandardError);
        }

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Sdk_transport_refuses_before_connect_even_without_Guild_ambient()
    {
        using IDisposable installed = WorkspaceMutationFence.Install(new ProtectedFence());

        RecordingTransport transport = new();

        await using SdkMcpClientWrapper client = new(transport, new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "test", Version = "1" },
        }, TimeSpan.FromSeconds(2), 4096, 4096, new McpElicitationSink());

        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => client.InitializeAsync());

        Assert.Contains(ErrorCodes.Guild.ToolBoundaryUnavailable, error.Message);

        Assert.Equal(0, transport.Starts);
    }

    [Fact]
    public async Task External_bridge_refuses_without_dispatch_or_fallback()
    {
        using IDisposable installed = WorkspaceMutationFence.Install(new ProtectedFence());

        RecordingClient client = new();

        using JsonDocument schema = JsonDocument.Parse("{\"type\":\"object\"}");

        McpBridgeTool tool = new("read_only_hint_is_not_proof", "read-only", schema.RootElement, client, 4096, fallbackClient: client);

        object? result = await tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.Contains(ErrorCodes.Guild.ToolBoundaryUnavailable, result?.ToString());

        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Uncontained_process_exit_does_not_enable_later_installation()
    {
        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            MarkerProcess(Path.Combine(_workspace.Root, "earlier-process")), ChildProcessEnvironmentProfile.ToolExec,
            4096, TimeSpan.FromSeconds(10), null, null, CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
        {
            using IDisposable installed = WorkspaceMutationFence.Install(new ProtectedFence());
        });

        Assert.Contains(ErrorCodes.Guild.ToolBoundaryUnavailable, error.Message);
    }

    [Fact]
    public void Pending_effect_and_installation_are_mutually_exclusive_before_dispatch()
    {
        Assert.True(WorkspaceMutationFence.TryBeginOpaqueEffect(out WorkspaceMutationFence.OpaqueEffect? effect));

        using (effect)
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                using IDisposable installed = WorkspaceMutationFence.Install(new ProtectedFence());
            });
        }

        using IDisposable registration = WorkspaceMutationFence.Install(new ProtectedFence());

        Assert.False(WorkspaceMutationFence.TryBeginOpaqueEffect(out _));
    }

    [Fact]
    public async Task Installed_fence_refuses_workspace_check_before_jail_probe()
    {
        using IDisposable installed = WorkspaceMutationFence.Install(new ProtectedFence());

        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        int probes = 0;

        WorkspaceCheckRuntime runtime = new(new WorkspaceCheckSettings { Enabled = true },
            services.GetRequiredService<IServiceScopeFactory>(), mandatoryJailAvailability: () =>
            {
                probes++;

                return false;
            });

        WorkspaceCheckExecutionStatus status = runtime.GetStatus(_workspace.Root);

        Assert.False(status.IsEligible);

        Assert.Equal(0, probes);

        Assert.Contains(ErrorCodes.Guild.ToolBoundaryUnavailable, status.Reason);

        var run = await runtime.RunAsync(new(_workspace.Root, "build", new Dictionary<string, string>()), CancellationToken.None);

        Assert.Equal(ErrorCodes.Guild.ToolBoundaryUnavailable, run.Code);

        Assert.Equal(0, probes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retired_registration_keeps_exclusion_until_leases_drain_and_recovery_is_proved(bool uncertain)
    {
        using IDisposable registration = WorkspaceMutationFence.Install(new HoldingFence());

        Result<IWorkspaceMutationLease> admitted = await WorkspaceMutationFence.AdmitAsync(_workspace.Root, [], CancellationToken.None);

        Assert.True(admitted.IsSuccess);

        await using IWorkspaceMutationLease lease = admitted.Value;

        if (uncertain)
        {
            Assert.True((await lease.RetainRecoveryAsync("unknown-effect", CancellationToken.None)).IsSuccess);
        }

        registration.Dispose();

        bool enteredBeforeDrain = WorkspaceMutationFence.TryBeginOpaqueEffect(out WorkspaceMutationFence.OpaqueEffect? beforeDrain);

        beforeDrain?.Dispose();

        Assert.False(enteredBeforeDrain);

        await lease.DisposeAsync();

        bool enteredAfterDrain = WorkspaceMutationFence.TryBeginOpaqueEffect(out WorkspaceMutationFence.OpaqueEffect? afterDrain);

        afterDrain?.Dispose();

        Assert.Equal(!uncertain, enteredAfterDrain);
    }

    [Fact]
    public async Task Composite_entry_failure_unwinds_previously_entered_scopes()
    {
        int entered = 0;

        using IDisposable first = WorkspaceMutationFence.Install(new HoldingFence(new HeldLease(
            () => entered++, () => entered--)));

        using IDisposable second = WorkspaceMutationFence.Install(new HoldingFence(new HeldLease(failEnter: true)));

        Result<IWorkspaceMutationLease> admitted = await WorkspaceMutationFence.AdmitAsync(_workspace.Root, [], CancellationToken.None);

        await using IWorkspaceMutationLease lease = admitted.Value;

        Assert.Throws<InvalidOperationException>(() => lease.Enter());

        Assert.Equal(0, entered);
    }

    [Fact]
    public async Task Disposed_composite_rejects_stale_validation_and_ambient_entry()
    {
        using IDisposable registration = WorkspaceMutationFence.Install(new HoldingFence());

        Result<IWorkspaceMutationLease> admitted = await WorkspaceMutationFence.AdmitAsync(_workspace.Root, [], CancellationToken.None);

        await admitted.Value.DisposeAsync();

        Assert.True((await admitted.Value.ValidateAsync(false, CancellationToken.None)).IsFailure);

        Assert.Throws<ObjectDisposedException>(() => admitted.Value.Enter());
    }

    [Fact]
    public async Task Unprotected_managed_lease_must_drain_before_installation()
    {
        Result<IWorkspaceMutationLease> admitted = await WorkspaceMutationFence.AdmitAsync(_workspace.Root, [], CancellationToken.None);

        await using IWorkspaceMutationLease lease = admitted.Value;

        Assert.Throws<InvalidOperationException>(() =>
        {
            using IDisposable installed = WorkspaceMutationFence.Install(new ProtectedFence());
        });

        await lease.DisposeAsync();

        using IDisposable registration = WorkspaceMutationFence.Install(new ProtectedFence());

        Assert.True(WorkspaceMutationFence.IsInstalled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_effect_adoption_reaches_every_participant_after_an_earlier_refusal(bool removal)
    {
        int laterAdoptions = 0;

        using IDisposable first = WorkspaceMutationFence.Install(new HoldingFence(new HeldLease(adopt: () =>
            ValueTask.FromResult(Result.Failure(new Error(ErrorCodes.Guild.WorkspaceIdentityUnavailable, "First participant refused adoption."))))));

        using IDisposable second = WorkspaceMutationFence.Install(new HoldingFence(new HeldLease(adopt: () =>
        {
            laterAdoptions++;

            return ValueTask.FromResult(Result.Success());
        })));

        Result<IWorkspaceMutationLease> admitted = await WorkspaceMutationFence.AdmitAsync(_workspace.Root, [], CancellationToken.None);

        await using IWorkspaceMutationLease lease = admitted.Value;

        Result result = removal
            ? await lease.AdoptRemovedDirectoryAsync(null!, false, CancellationToken.None)
            : await lease.AdoptCreatedDirectoryAsync(null!, false, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(1, laterAdoptions);
    }

    private sealed class HoldingFence(IWorkspaceMutationLease? lease = null) : IWorkspaceMutationFence
    {
        public ValueTask<Result<IWorkspaceMutationLease>> AdmitAsync(string workspaceRoot,
            ImmutableArray<GuildPathScope> scopes, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<IWorkspaceMutationLease>.Success(lease ?? new HeldLease()));
    }

    private sealed class HeldLease(Action? onEnter = null, Action? onExit = null, bool failEnter = false, Func<ValueTask<Result>>? adopt = null) : IWorkspaceMutationLease
    {
        public ValueTask<Result> ValidateDirectoryCreationAsync(WorkspaceLeaseDirectoryCreation preparation, bool rollback, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> AdoptCreatedDirectoryAsync(WorkspaceLeaseCreatedDirectory proof, bool rollback, CancellationToken cancellationToken) => adopt?.Invoke() ?? ValueTask.FromResult(Result.Success());

        public ValueTask<Result> ValidateDirectoryRemovalAsync(WorkspaceLeaseDirectoryRemoval preparation, bool rollback, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> AdoptRemovedDirectoryAsync(WorkspaceLeaseRemovedDirectory proof, bool rollback, CancellationToken cancellationToken) => adopt?.Invoke() ?? ValueTask.FromResult(Result.Success());

        public ValueTask<Result> ValidateAsync(bool rollback, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> RetainRecoveryAsync(string reasonCode, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public IDisposable Enter()
        {
            if (failEnter)
            {
                throw new InvalidOperationException("entry refused");
            }

            onEnter?.Invoke();

            return new EmptyScope(onExit);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyScope(Action? onExit = null) : IDisposable
    {
        public void Dispose() => onExit?.Invoke();
    }

    private sealed class RecordingClient : IMcpClient
    {
        internal int Calls { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpBridgeTool>>([]);

        public Task<CallToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments,
            TimeSpan? requestTimeout = null, CancellationToken cancellationToken = default)
        {
            Calls++;

            return Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "dispatched" }] });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Direct_sdk_call_refuses_before_session_lookup_when_fence_is_installed()
    {
        using IDisposable installed = WorkspaceMutationFence.Install(new ProtectedFence());

        RecordingTransport transport = new();

        await using SdkMcpClientWrapper client = new(transport, new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "test", Version = "1" },
        }, TimeSpan.FromSeconds(2), 4096, 4096, new McpElicitationSink());

        CallToolResult result = await client.CallToolAsync("opaque", new Dictionary<string, object?>());

        Assert.True(result.IsError);

        Assert.Contains(ErrorCodes.Guild.ToolBoundaryUnavailable, McpToolResultFormatter.FormatContentText(result));

        Assert.Equal(0, transport.Starts);
    }

    internal sealed class ProtectedFence : IWorkspaceMutationFence
    {
        public ValueTask<Result<IWorkspaceMutationLease>> AdmitAsync(string workspaceRoot,
            ImmutableArray<GuildPathScope> scopes, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Result<IWorkspaceMutationLease>>(new Error(ErrorCodes.Guild.LeaseConflict, "Protected workspace."));
    }

    private sealed class RecordingTransport : IClientTransport
    {
        internal int Starts { get; private set; }

        public string Name => "external";

        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
        {
            Starts++;

            throw new InvalidOperationException("Transport was started.");
        }
    }

    private static ProcessStartInfo MarkerProcess(string marker)
    {
        ProcessStartInfo start = new()
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("[System.IO.File]::WriteAllText($args[0], 'started')");
            start.ArgumentList.Add(marker);
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("printf started > \"$1\"");
            start.ArgumentList.Add("marker");
            start.ArgumentList.Add(marker);
        }

        return start;
    }
}
