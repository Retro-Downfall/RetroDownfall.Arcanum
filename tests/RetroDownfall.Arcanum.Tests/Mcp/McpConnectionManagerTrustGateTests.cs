using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using CallToolResult = ModelContextProtocol.Protocol.CallToolResult;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Support;
using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Tests.Mcp;

[Collection("ProcessEnvironment")]
public sealed class McpConnectionManagerTrustGateTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    private string? _originalDotnetEnvironment;

    private string? _originalAspNetCoreEnvironment;

    private string? _originalTestHome;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _originalDotnetEnvironment =
            global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        _originalAspNetCoreEnvironment =
            global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        _originalTestHome =
            global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");
        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _workspace.Root);

        Assert.StartsWith(
            Path.GetFullPath(_workspace.Root),
            Path.GetFullPath(ArcanumPaths.GlobalMcpConfigFile),
            StringComparison.Ordinal);

        _workspace.WriteFile(
            "mcp.json",
            """
            {
              "mcpServers": {
                "untrusted-local": {
                  "command": "echo",
                  "args": ["trusted-gate-test"]
                }
              }
            }
            """);

    }

    public async Task DisposeAsync()
    {

        global::System.Environment.SetEnvironmentVariable(
            "DOTNET_ENVIRONMENT",
            _originalDotnetEnvironment);
        global::System.Environment.SetEnvironmentVariable(
            "ASPNETCORE_ENVIRONMENT",
            _originalAspNetCoreEnvironment);
        global::System.Environment.SetEnvironmentVariable(
            "ARCANUM_TEST_HOME",
            _originalTestHome);

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task GetAvailableToolsAsync_untrusted_workspace_does_not_register_local_servers()
    {

        await using McpConnectionManager manager = CreateManager(new UntrustedWorkspaceStore());

        await manager.GetAvailableToolsAsync(_workspace.Root);

        McpServerInfo? status = await manager.GetStatusAsync("untrusted-local", _workspace.Root);

        Assert.Null(status);

    }

    [Fact]
    public async Task RestartAsync_after_trust_revoke_returns_WorkspaceNotTrusted()
    {

        ToggleableTrustStore trust = new() { Trusted = true };

        await using McpConnectionManager manager = CreateManager(trust);

        _workspace.WriteFile(
            "mcp.json",
            """
            {
              "mcpServers": {
                "local-restart": {
                  "command": "arcanum-nonexistent-binary-zzz",
                  "alwaysOn": false
                }
              }
            }
            """);

        await manager.GetAvailableToolsAsync(_workspace.Root);

        Result start = await manager.StartAsync("local-restart", _workspace.Root);

        Assert.True(start.IsFailure);

        Assert.Equal("Mcp.StartFailed", start.Error.Code);

        McpServerInfo? afterStart = await manager.GetStatusAsync("local-restart", _workspace.Root);

        Assert.NotNull(afterStart);

        Assert.Equal(McpServerState.Error, afterStart!.State);

        trust.Trusted = false;

        Result restart = await manager.RestartAsync("local-restart", _workspace.Root);

        Assert.True(restart.IsFailure);

        Assert.Equal("Mcp.WorkspaceNotTrusted", restart.Error.Code);

        Assert.Contains("trust-workspace", restart.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Workspace_admission_uses_digest_of_the_exact_bytes_parsed()
    {

        const string config =
            """
            {
              "mcpServers": {
                "digest-bound": {
                  "type": "sse",
                  "url": "https://example.test/mcp",
                  "alwaysOn": false
                }
              }
            }
            """;

        string path = _workspace.WriteFile("mcp.json", config);

        string expectedDigest = await ComputeSha256HexAsync(path);

        DigestTrustStore trust = new() { ApprovedDigest = expectedDigest };

        await using McpConnectionManager manager = CreateManager(trust);

        await manager.GetAvailableToolsAsync(_workspace.Root);

        Assert.Equal(expectedDigest, trust.LastAdmissionDigest);

        Assert.NotNull(await manager.GetStatusAsync("digest-bound", _workspace.Root));

    }

    [Fact]
    public async Task Read_then_trust_swap_cannot_authorize_previously_parsed_bytes()
    {

        const string configA =
            """
            {
              "mcpServers": {
                "config-a": {
                  "type": "sse",
                  "url": "https://a.example.test/mcp",
                  "alwaysOn": false
                }
              }
            }
            """;

        const string configB =
            """
            {
              "mcpServers": {
                "config-b": {
                  "type": "sse",
                  "url": "https://b.example.test/mcp",
                  "alwaysOn": false
                }
              }
            }
            """;

        string path = _workspace.WriteFile("mcp.json", configA);

        string digestA = await ComputeSha256HexAsync(path);

        DigestTrustStore trust = new() { ApprovedDigest = digestA };

        trust.BeforeAdmission = () =>
        {
            File.WriteAllText(path, configB);

            trust.ApprovedDigest = ComputeSha256Hex(path);
        };

        await using McpConnectionManager manager = CreateManager(trust);

        await manager.GetAvailableToolsAsync(_workspace.Root);

        Assert.Equal(digestA, trust.LastAdmissionDigest);

        Assert.Null(await manager.GetStatusAsync("config-a", _workspace.Root));

        Assert.Null(await manager.GetStatusAsync("config-b", _workspace.Root));

    }

    [Fact]
    public async Task Config_B_retires_same_name_config_A_and_removed_entries()
    {

        const string configA =
            """
            {
              "mcpServers": {
                "same-name": {
                  "command": "arcanum-nonexistent-binary-zzz",
                  "alwaysOn": false
                },
                "removed": {
                  "type": "sse",
                  "url": "https://removed.example.test/mcp",
                  "alwaysOn": false
                }
              }
            }
            """;

        const string configB =
            """
            {
              "mcpServers": {
                "same-name": {
                  "type": "sse",
                  "url": "https://replacement.example.test/mcp",
                  "alwaysOn": false
                }
              }
            }
            """;

        string path = _workspace.WriteFile("mcp.json", configA);

        DigestTrustStore trust = new() { ApprovedDigest = await ComputeSha256HexAsync(path) };

        await using McpConnectionManager manager = CreateManager(trust);

        await manager.GetAvailableToolsAsync(_workspace.Root);

        Assert.NotNull(await manager.GetStatusAsync("same-name", _workspace.Root));

        Assert.NotNull(await manager.GetStatusAsync("removed", _workspace.Root));

        File.WriteAllText(path, configB);

        trust.ApprovedDigest = await ComputeSha256HexAsync(path);

        await manager.GetAvailableToolsAsync(_workspace.Root);

        Result replacementStart = await manager.StartAsync("same-name", _workspace.Root);

        Assert.True(replacementStart.IsFailure);

        Assert.Equal("Mcp.SseNotSupported", replacementStart.Error.Code);

        Result removedStart = await manager.StartAsync("removed", _workspace.Root);

        Assert.True(removedStart.IsFailure);

        Assert.Equal(ErrorCodes.Mcp.ServerNotFound, removedStart.Error.Code);

    }

    [Fact]
    public async Task GetAllStatuses_reuses_one_workspace_trust_snapshot_for_all_entries()
    {

        string path = _workspace.WriteFile(
            "mcp.json",
            """
            {
              "mcpServers": {
                "first": { "type": "sse", "url": "https://first.example.test/mcp" },
                "second": { "type": "sse", "url": "https://second.example.test/mcp" }
              }
            }
            """);

        DigestTrustStore trust = new() { ApprovedDigest = await ComputeSha256HexAsync(path) };

        await using McpConnectionManager manager = CreateManager(trust);

        await manager.GetAvailableToolsAsync(_workspace.Root);

        trust.SnapshotCalls = 0;

        McpServerInfo[] statuses = await manager.GetAllStatusesAsync();

        Assert.Equal(2, statuses.Length);
        Assert.Equal(1, trust.SnapshotCalls);

    }

    [Fact]
    public async Task Workspace_surface_cache_is_reused_while_its_digest_remains_trusted()
    {

        string path = _workspace.WriteFile(
            "mcp.json",
            """
            {
              "mcpServers": {
                "cached": { "type": "sse", "url": "https://cached.example.test/mcp" }
              }
            }
            """);

        DigestTrustStore trust = new() { ApprovedDigest = await ComputeSha256HexAsync(path) };

        await using McpConnectionManager manager = CreateManager(trust);

        await manager.GetAvailableToolsAsync(_workspace.Root);
        await manager.GetAvailableToolsAsync(_workspace.Root);

        Assert.Equal(1, trust.AdmissionCalls);
        Assert.Equal(1, trust.SnapshotCalls);

    }

    [Fact]
    public async Task Generation_move_during_a_workspace_build_leaves_internal_tools_callable()
    {

        string path = _workspace.WriteFile(
            "mcp.json",
            """
            {
              "mcpServers": {
                "workspace-local": {
                  "type": "sse",
                  "url": "https://workspace.example.test/mcp",
                  "alwaysOn": false
                }
              }
            }
            """);

        DigestTrustStore trust = new()
        {
            ApprovedDigest =
                await ComputeSha256HexAsync(path),
        };

        await using McpConnectionManager manager =
            CreateManager(trust);

        await manager.RegisterFromConfigAsync(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>
                {
                    ["generation-source"] = new()
                    {
                        Type = "sse",
                        Url = "https://generation.example.test/mcp",
                        AlwaysOn = false,
                    },
                },
            },
            scopeWorkingDirectory: null,
            CancellationToken.None);

        ManagedMcpServerEntry bumper =
            Assert.IsType<ManagedMcpServerEntry>(
                manager.GetManagedEntryForTests(
                    "generation-source",
                    null));

        bumper.Client = new InertMcpClient();
        bumper.State = McpServerState.Running;

        // Production reaches this on the very first build for any workspace whose trusted mcp.json
        // carries an alwaysOn server: the successful start moves the tool-surface generation from
        // inside the build, after the partition's in-process arcanum-internal client is cached.
        trust.BeforeAdmissionAsync = () =>
            manager.StopAsync("generation-source", null);

        IReadOnlyList<Microsoft.Extensions.AI.AITool> tools =
            await manager.GetAvailableToolsAsync(_workspace.Root);

        Microsoft.Extensions.AI.AIFunction listDirectory =
            Assert.IsAssignableFrom<Microsoft.Extensions.AI.AIFunction>(
                tools.Single(
                    tool => string.Equals(
                        tool.Name,
                        "list_directory",
                        StringComparison.Ordinal)));

        object? listed = await listDirectory.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["relativePath"] = ".",
                }),
            CancellationToken.None);

        Assert.NotNull(listed);

    }

    [Fact]
    public async Task Retiring_running_entry_releases_registry_lock_before_blocked_disposal()
    {

        const string configA =
            """
            {
              "mcpServers": {
                "running": { "type": "sse", "url": "https://running.example.test/mcp" }
              }
            }
            """;
        const string configB = """{"mcpServers":{}}""";

        string path = _workspace.WriteFile("mcp.json", configA);
        DigestTrustStore trust = new() { ApprovedDigest = await ComputeSha256HexAsync(path) };

        await using McpConnectionManager manager = CreateManager(trust);

        await manager.GetAvailableToolsAsync(_workspace.Root);

        ManagedMcpServerEntry entry =
            Assert.IsType<ManagedMcpServerEntry>(
                manager.GetManagedEntryForTests("running", _workspace.Root));
        BlockingDisposeClient client = new();

        entry.Client = client;
        entry.State = McpServerState.Running;

        await File.WriteAllTextAsync(path, configB);
        trust.ApprovedDigest = await ComputeSha256HexAsync(path);

        Task refresh = manager.GetAvailableToolsAsync(_workspace.Root);

        await client.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task registration = manager.RegisterFromConfigAsync(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>
                {
                    ["unrelated-global"] = new()
                    {
                        Type = "sse",
                        Url = "https://global.example.test/mcp",
                    },
                },
            },
            scopeWorkingDirectory: null,
            CancellationToken.None);

        await registration.WaitAsync(TimeSpan.FromSeconds(1));

        client.AllowDispose.TrySetResult();

        await refresh.WaitAsync(TimeSpan.FromSeconds(5));

    }

    [Fact]
    public async Task Canceled_refresh_tracks_blocked_retirement_until_manager_disposal()
    {
        const string configA =
            """
            {
              "mcpServers": {
                "retired-running": { "type": "sse", "url": "https://running.example.test/mcp" }
              }
            }
            """;
        const string configB = """{"mcpServers":{}}""";

        string path = _workspace.WriteFile(
            "mcp.json",
            configA);

        DigestTrustStore trust = new()
        {
            ApprovedDigest =
                await ComputeSha256HexAsync(path),
        };

        McpConnectionManager manager = CreateManager(trust);
        BlockingDisposeClient client = new();

        try
        {
            await manager.GetAvailableToolsAsync(
                _workspace.Root);

            ManagedMcpServerEntry entry =
                Assert.IsType<ManagedMcpServerEntry>(
                    manager.GetManagedEntryForTests(
                        "retired-running",
                        _workspace.Root));

            entry.Client = client;
            entry.State = McpServerState.Running;

            await File.WriteAllTextAsync(
                path,
                configB);

            trust.ApprovedDigest =
                await ComputeSha256HexAsync(path);

            using CancellationTokenSource cancellation = new();

            Task refresh = manager.GetAvailableToolsAsync(
                _workspace.Root,
                cancellation.Token);

            await client.DisposeStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => refresh);

            Assert.Null(
                await manager.GetStatusAsync(
                    "retired-running",
                    _workspace.Root));

            Assert.True(
                GetPendingRetirementCount(manager) > 0);

            Task managerDisposal =
                manager.DisposeAsync().AsTask();

            Assert.False(managerDisposal.IsCompleted);

            client.AllowDispose.TrySetResult();

            await managerDisposal.WaitAsync(
                TimeSpan.FromSeconds(5));

            await client.DisposeCompleted.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            client.AllowDispose.TrySetResult();

            await manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task Canceled_replacement_refresh_keeps_replacement_hidden_until_retirement_finishes()
    {
        const string configA =
            """
            {
              "mcpServers": {
                "same-name": {
                  "type": "sse",
                  "url": "https://config-a.example.test/mcp",
                  "alwaysOn": false
                }
              }
            }
            """;
        const string configB =
            """
            {
              "mcpServers": {
                "same-name": {
                  "type": "sse",
                  "url": "https://config-b.example.test/mcp",
                  "alwaysOn": false
                }
              }
            }
            """;

        string path = _workspace.WriteFile(
            "mcp.json",
            configA);
        DigestTrustStore trust = new()
        {
            ApprovedDigest =
                await ComputeSha256HexAsync(path),
        };

        await using McpConnectionManager manager =
            CreateManager(trust);
        BlockingDisposeClient client = new();

        try
        {
            await manager.GetAvailableToolsAsync(
                _workspace.Root);

            ManagedMcpServerEntry entry =
                Assert.IsType<ManagedMcpServerEntry>(
                    manager.GetManagedEntryForTests(
                        "same-name",
                        _workspace.Root));
            entry.Client = client;
            entry.State = McpServerState.Running;

            await File.WriteAllTextAsync(
                path,
                configB);
            trust.ApprovedDigest =
                await ComputeSha256HexAsync(path);

            using CancellationTokenSource cancellation = new();
            Task refresh = manager.GetAvailableToolsAsync(
                _workspace.Root,
                cancellation.Token);

            await client.DisposeStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => refresh);

            Assert.Null(
                manager.GetManagedEntryForTests(
                    "same-name",
                    _workspace.Root));

            Result earlyStart = await manager.StartAsync(
                "same-name",
                _workspace.Root);

            Assert.True(earlyStart.IsFailure);
            Assert.Equal(
                ErrorCodes.Mcp.ServerNotFound,
                earlyStart.Error.Code);

            Task<IReadOnlyList<Microsoft.Extensions.AI.AITool>>
                concurrentGetTools =
                    manager.GetAvailableToolsAsync(
                        _workspace.Root);

            Assert.False(concurrentGetTools.IsCompleted);

            client.AllowDispose.TrySetResult();

            _ = await concurrentGetTools.WaitAsync(
                TimeSpan.FromSeconds(5));
            await client.DisposeCompleted.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            ManagedMcpServerEntry replacement =
                Assert.IsType<ManagedMcpServerEntry>(
                    manager.GetManagedEntryForTests(
                        "same-name",
                        _workspace.Root));

            Assert.Equal(
                "https://config-b.example.test/mcp",
                replacement.Config.Url);

            Result replacementStart = await manager.StartAsync(
                "same-name",
                _workspace.Root);

            Assert.True(replacementStart.IsFailure);
            Assert.Equal(
                "Mcp.SseNotSupported",
                replacementStart.Error.Code);
        }
        finally
        {
            client.AllowDispose.TrySetResult();
        }
    }

    [Fact]
    public async Task Canceled_stop_tracks_disposal_and_blocks_restart_until_old_client_exits()
    {
        const string config =
            """
            {
              "mcpServers": {
                "stoppable": { "type": "sse", "url": "https://stop.example.test/mcp" }
              }
            }
            """;

        string path = _workspace.WriteFile(
            "mcp.json",
            config);

        DigestTrustStore trust = new()
        {
            ApprovedDigest =
                await ComputeSha256HexAsync(path),
        };

        await using McpConnectionManager manager =
            CreateManager(trust);

        await manager.GetAvailableToolsAsync(
            _workspace.Root);

        ManagedMcpServerEntry entry =
            Assert.IsType<ManagedMcpServerEntry>(
                manager.GetManagedEntryForTests(
                    "stoppable",
                    _workspace.Root));

        BlockingDisposeClient client = new();
        entry.Client = client;
        entry.State = McpServerState.Running;

        using CancellationTokenSource cancellation = new();

        Task<Result> stop = manager.StopAsync(
            "stoppable",
            _workspace.Root,
            cancellation.Token);

        await client.DisposeStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        Assert.False(stop.IsCompleted);
        Assert.True(
            GetPendingRetirementCount(manager) > 0);

        Task<Result> restart = manager.RestartAsync(
            "stoppable",
            _workspace.Root);

        Assert.False(restart.IsCompleted);

        client.AllowDispose.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stop);

        _ = await restart.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.True(client.DisposeCompleted.Task.IsCompleted);
    }

    [Fact]
    public async Task Canceled_restart_tracks_disposal_and_prevents_overlapping_restart()
    {
        const string config =
            """
            {
              "mcpServers": {
                "restartable": { "type": "sse", "url": "https://restart.example.test/mcp" }
              }
            }
            """;

        string path = _workspace.WriteFile(
            "mcp.json",
            config);

        DigestTrustStore trust = new()
        {
            ApprovedDigest =
                await ComputeSha256HexAsync(path),
        };

        await using McpConnectionManager manager =
            CreateManager(trust);

        await manager.GetAvailableToolsAsync(
            _workspace.Root);

        ManagedMcpServerEntry entry =
            Assert.IsType<ManagedMcpServerEntry>(
                manager.GetManagedEntryForTests(
                    "restartable",
                    _workspace.Root));

        BlockingDisposeClient client = new();
        entry.Client = client;
        entry.State = McpServerState.Running;

        using CancellationTokenSource cancellation = new();

        Task<Result> canceledRestart =
            manager.RestartAsync(
                "restartable",
                _workspace.Root,
                cancellation.Token);

        await client.DisposeStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        Assert.False(canceledRestart.IsCompleted);
        Assert.True(
            GetPendingRetirementCount(manager) > 0);

        Task<Result> overlappingRestart =
            manager.RestartAsync(
                "restartable",
                _workspace.Root);

        Assert.False(overlappingRestart.IsCompleted);

        client.AllowDispose.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceledRestart);

        _ = await overlappingRestart.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.True(client.DisposeCompleted.Task.IsCompleted);
    }

    [Fact]
    public async Task Start_resolved_before_retirement_cannot_restart_removed_entry()
    {

        const string configA =
            """
            {
              "mcpServers": {
                "removed-during-start": {
                  "type": "sse",
                  "url": "https://removed.example.test/mcp"
                }
              }
            }
            """;
        const string configB = """{"mcpServers":{}}""";

        string path = _workspace.WriteFile("mcp.json", configA);
        DigestTrustStore trust = new() { ApprovedDigest = await ComputeSha256HexAsync(path) };

        await using McpConnectionManager manager = CreateManager(trust);

        await manager.GetAvailableToolsAsync(_workspace.Root);

        ManagedMcpServerEntry entry =
            Assert.IsType<ManagedMcpServerEntry>(
                manager.GetManagedEntryForTests("removed-during-start", _workspace.Root));
        TaskCompletionSource snapshotEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allowSnapshot =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        trust.BeforeSnapshotReturn = async (_, cancellationToken) =>
        {
            snapshotEntered.TrySetResult();
            await allowSnapshot.Task.WaitAsync(cancellationToken);
        };

        Task<Result> start =
            manager.StartAsync("removed-during-start", _workspace.Root);

        await snapshotEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await File.WriteAllTextAsync(path, configB);
        trust.ApprovedDigest = await ComputeSha256HexAsync(path);

        Task refresh = manager.GetAvailableToolsAsync(_workspace.Root);

        await WaitUntilAsync(() => entry.IsRetired, TimeSpan.FromSeconds(5));

        allowSnapshot.TrySetResult();

        Result startResult = await start.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(startResult.IsFailure);
        Assert.Equal(ErrorCodes.Mcp.ServerNotFound, startResult.Error.Code);

        await refresh.WaitAsync(TimeSpan.FromSeconds(5));

    }

    [Fact]
    public async Task Oversized_workspace_config_retires_previously_registered_entries()
    {

        const string config =
            """
            {
              "mcpServers": {
                "retired-after-oversize": {
                  "type": "sse",
                  "url": "https://example.test/mcp",
                  "alwaysOn": false
                }
              }
            }
            """;

        string path = _workspace.WriteFile("mcp.json", config);

        DigestTrustStore trust = new() { ApprovedDigest = await ComputeSha256HexAsync(path) };

        await using McpConnectionManager manager = CreateManager(trust);

        await manager.GetAvailableToolsAsync(_workspace.Root);

        Assert.NotNull(await manager.GetStatusAsync("retired-after-oversize", _workspace.Root));

        await File.WriteAllBytesAsync(
            path,
            Enumerable.Repeat((byte)' ', McpSecurityLimits.MaxMcpConfigBytes + 1).ToArray());

        await manager.GetAvailableToolsAsync(_workspace.Root);

        Result start = await manager.StartAsync("retired-after-oversize", _workspace.Root);

        Assert.True(start.IsFailure);

        Assert.Equal(ErrorCodes.Mcp.ServerNotFound, start.Error.Code);

    }

    [Fact]
    public async Task Global_entries_do_not_require_workspace_source_digest()
    {

        ThrowingTrustStore trust = new();

        await using McpConnectionManager manager = CreateManager(trust);

        await manager.RegisterFromConfigAsync(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>
                {
                    ["global"] = new()
                    {
                        Type = "sse",
                        Url = "https://example.test/mcp",
                    },
                },
            },
            scopeWorkingDirectory: null,
            CancellationToken.None);

        Result start = await manager.StartAsync("global", workingDirectory: null);

        Assert.True(start.IsFailure);

        Assert.Equal("Mcp.SseNotSupported", start.Error.Code);

    }

    [Fact]
    public async Task TrustWorkspaceAsync_returns_sanitized_actionable_store_failure()
    {

        await using McpConnectionManager manager = CreateManager(new FailingTrustStore());

        Result result = await manager.TrustWorkspaceAsync(_workspace.Root);

        Assert.True(result.IsFailure);

        Assert.Equal("Mcp.TrustFailed", result.Error.Code);

        Assert.Contains("Remove it", result.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(_workspace.Root, result.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task TrustWorkspaceAsync_preserves_cancellation()
    {

        await using McpConnectionManager manager = CreateManager(new CancelingTrustStore());

        using CancellationTokenSource canceled = new();

        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.TrustWorkspaceAsync(_workspace.Root, canceled.Token));

    }

    private McpConnectionManager CreateManager(ITrustedMcpWorkspaceStore trustStore)
    {

        ServiceCollection services = new();

        services.AddSingleton<ISanctumGuard, PermissiveSanctumGuard>();

        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<ArcanumSettings>>(
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings
                {
                    Security = new SecuritySettings { AllowUnsandboxedToolChildren = true },
                }));

        IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        IHumanPromptRegistry humanPrompts = new HumanPromptRegistry();

        IUnseenServantPacer pacer = new UnseenServantPacer(
            new FakeEventBus(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            scopeFactory,
            NullLogger<UnseenServantPacer>.Instance);

        return new McpConnectionManager(
            NullLogger<McpConnectionManager>.Instance,
            humanPrompts,
            scopeFactory,
            pacer,
            new FakeEventBus(),
            trustStore,
            new FakeHttpClientFactory(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

    }

    private sealed class UntrustedWorkspaceStore : ITrustedMcpWorkspaceStore
    {

        public Task<bool> IsTrustedAsync(string workspaceRootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> IsTrustedAsync(
            string workspaceRootPath,
            string sourceDigest,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> IsApprovedDigestAsync(
            string workspaceRootPath,
            string sourceDigest,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<TrustedMcpWorkspaceSnapshot> GetSnapshotAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(default(TrustedMcpWorkspaceSnapshot));

        public Task TrustAsync(string workspaceRootPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

    }

    private sealed class ToggleableTrustStore : ITrustedMcpWorkspaceStore
    {

        public bool Trusted { get; set; }

        public Task<bool> IsTrustedAsync(string workspaceRootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(Trusted);

        public Task<bool> IsTrustedAsync(
            string workspaceRootPath,
            string sourceDigest,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Trusted);

        public Task<bool> IsApprovedDigestAsync(
            string workspaceRootPath,
            string sourceDigest,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Trusted);

        public Task<TrustedMcpWorkspaceSnapshot> GetSnapshotAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string digest = ComputeSha256Hex(
                Path.Combine(workspaceRootPath, "mcp.json"));

            return Task.FromResult(
                new TrustedMcpWorkspaceSnapshot(digest, Trusted));
        }

        public Task TrustAsync(string workspaceRootPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

    }

    private sealed class DigestTrustStore : ITrustedMcpWorkspaceStore
    {

        public string ApprovedDigest { get; set; } = string.Empty;

        public string? LastAdmissionDigest { get; private set; }

        public Action? BeforeAdmission { get; set; }

        public Func<Task>? BeforeAdmissionAsync { get; set; }

        public int AdmissionCalls { get; private set; }

        public int SnapshotCalls { get; set; }

        public Func<TrustedMcpWorkspaceSnapshot, CancellationToken, Task>?
            BeforeSnapshotReturn
        { get; set; }

        public Task<bool> IsTrustedAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(string.Equals(
                ComputeSha256Hex(Path.Combine(workspaceRootPath, "mcp.json")),
                ApprovedDigest,
                StringComparison.OrdinalIgnoreCase));

        }

        public Task<bool> IsTrustedAsync(
            string workspaceRootPath,
            string sourceDigest,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            bool trusted = string.Equals(sourceDigest, ApprovedDigest, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    ComputeSha256Hex(Path.Combine(workspaceRootPath, "mcp.json")),
                    ApprovedDigest,
                    StringComparison.OrdinalIgnoreCase);

            return Task.FromResult(trusted);

        }

        public async Task<bool> IsApprovedDigestAsync(
            string workspaceRootPath,
            string sourceDigest,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            LastAdmissionDigest = sourceDigest;
            AdmissionCalls++;

            Action? beforeAdmission = BeforeAdmission;

            BeforeAdmission = null;

            beforeAdmission?.Invoke();

            Func<Task>? beforeAdmissionAsync = BeforeAdmissionAsync;

            BeforeAdmissionAsync = null;

            if (beforeAdmissionAsync is not null)
            {
                await beforeAdmissionAsync();
            }

            return string.Equals(
                sourceDigest,
                ApprovedDigest,
                StringComparison.OrdinalIgnoreCase);

        }

        public async Task<TrustedMcpWorkspaceSnapshot> GetSnapshotAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SnapshotCalls++;

            string currentDigest = ComputeSha256Hex(
                Path.Combine(workspaceRootPath, "mcp.json"));
            TrustedMcpWorkspaceSnapshot snapshot =
                new(
                    currentDigest,
                    string.Equals(
                        currentDigest,
                        ApprovedDigest,
                        StringComparison.OrdinalIgnoreCase));

            Func<TrustedMcpWorkspaceSnapshot, CancellationToken, Task>? beforeReturn =
                BeforeSnapshotReturn;
            BeforeSnapshotReturn = null;

            if (beforeReturn is not null)
            {
                await beforeReturn(snapshot, cancellationToken);
            }

            return snapshot;
        }

        public Task TrustAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            ApprovedDigest = ComputeSha256Hex(Path.Combine(workspaceRootPath, "mcp.json"));

            return Task.CompletedTask;

        }

    }

    private sealed class ThrowingTrustStore : ITrustedMcpWorkspaceStore
    {

        public Task<bool> IsTrustedAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Global entries must not query workspace trust.");

        public Task<bool> IsTrustedAsync(
            string workspaceRootPath,
            string sourceDigest,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Global entries must not query workspace trust.");

        public Task<bool> IsApprovedDigestAsync(
            string workspaceRootPath,
            string sourceDigest,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Global entries must not query workspace trust.");

        public Task<TrustedMcpWorkspaceSnapshot> GetSnapshotAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Global entries must not query workspace trust.");

        public Task TrustAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Global entries must not query workspace trust.");

    }

    private sealed class FailingTrustStore : ITrustedMcpWorkspaceStore
    {

        public Task<bool> IsTrustedAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> IsTrustedAsync(
            string workspaceRootPath,
            string sourceDigest,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> IsApprovedDigestAsync(
            string workspaceRootPath,
            string sourceDigest,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<TrustedMcpWorkspaceSnapshot> GetSnapshotAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(default(TrustedMcpWorkspaceSnapshot));

        public Task TrustAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default) =>
            throw new TrustedMcpWorkspaceStoreException(
                "The MCP approval store is corrupt. Remove it and retry.");

    }

    private sealed class CancelingTrustStore : ITrustedMcpWorkspaceStore
    {

        public Task<bool> IsTrustedAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default) =>
            Task.FromCanceled<bool>(cancellationToken);

        public Task<bool> IsTrustedAsync(
            string workspaceRootPath,
            string sourceDigest,
            CancellationToken cancellationToken = default) =>
            Task.FromCanceled<bool>(cancellationToken);

        public Task<bool> IsApprovedDigestAsync(
            string workspaceRootPath,
            string sourceDigest,
            CancellationToken cancellationToken = default) =>
            Task.FromCanceled<bool>(cancellationToken);

        public Task<TrustedMcpWorkspaceSnapshot> GetSnapshotAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default) =>
            Task.FromCanceled<TrustedMcpWorkspaceSnapshot>(cancellationToken);

        public Task TrustAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default) =>
            Task.FromCanceled(cancellationToken);

    }

    private static async Task<string> ComputeSha256HexAsync(string path)
    {

        byte[] bytes = await File.ReadAllBytesAsync(path);

        return Convert.ToHexString(SHA256.HashData(bytes));

    }

    private static string ComputeSha256Hex(string path)
    {

        byte[] bytes = File.ReadAllBytes(path);

        return Convert.ToHexString(SHA256.HashData(bytes));

    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the test condition.");
            }

            await Task.Delay(10);
        }
    }

    private static int GetPendingRetirementCount(
        McpConnectionManager manager)
    {
        System.Reflection.FieldInfo? field =
            typeof(McpConnectionManager).GetField(
                "_pendingWorkspaceRetirements",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(field);

        object pending = Assert.IsAssignableFrom<object>(
            field.GetValue(manager));

        System.Reflection.PropertyInfo? count =
            pending.GetType().GetProperty("Count");

        Assert.NotNull(count);

        return Assert.IsType<int>(
            count.GetValue(pending));
    }

    private sealed class BlockingDisposeClient : IMcpClient
    {
        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowDispose { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DisposeCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpBridgeTool>>([]);

        public Task<CallToolResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            TimeSpan? requestTimeout = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async ValueTask DisposeAsync()
        {
            DisposeStarted.TrySetResult();
            await AllowDispose.Task.WaitAsync(TimeSpan.FromSeconds(30));
            DisposeCompleted.TrySetResult();
        }
    }

    private sealed class InertMcpClient : IMcpClient
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpBridgeTool>>([]);

        public Task<CallToolResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            TimeSpan? requestTimeout = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PermissiveSanctumGuard : ISanctumGuard
    {

        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateToolAsync(string campaignId, string toolName, CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult(new ResourceLimits());

        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult<SanctumChildProcessBoundary?>(null);

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            Core.Platform.ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            Task.CompletedTask;

    }

    private sealed class FakeEventBus : IEventBus
    {

        public void Publish<T>(T @event) where T : notnull
        {
        }

        public IAsyncEnumerable<T> Subscribe<T>(CancellationToken cancellationToken) where T : notnull =>
            AsyncEnumerable.Empty<T>();

    }

}
