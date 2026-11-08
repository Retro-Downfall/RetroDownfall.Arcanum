using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

[Collection("WorkspacePathPolicy")]
public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Guild_host_readiness_physical_stage_refuses_closed_effect_admission_after_identity_commit()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        RecordingGrimoireWorkAdmissionGate physicalAdmission = new(f.Gate);

        IGrimoireClosingOwner? closing = null;

        physicalAdmission.BeforeEffectGroupAdmission = () =>
        {
            Assert.NotEqual(Guid.Empty, f.Policy.OperatorPrincipalId);

            var begun = f.Gate.BeginOrResumeExclusive(GrimoireGateTestEdges.Owner(150));

            Assert.True(begun.IsSuccess, begun.Error.Message);

            closing = begun.Value;
        };

        string path = Path.Combine(f.WorkspaceRoot, "host.lock");

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, path, physicalAdmission);

        try
        {
            var ready = await readiness.EnsureReadyAsync(default);

            Assert.True(ready.IsFailure);

            Assert.Equal(1, physicalAdmission.EffectGroupAttempts);

            Assert.NotEqual(Guid.Empty, f.Policy.OperatorPrincipalId);

            Assert.False(File.Exists(path));

            Assert.False(WorkspaceMutationFence.IsInstalled);

            Assert.True(readiness.RequireExecutionReady().IsFailure);
        }
        finally
        {
            if (closing is not null) await closing.DisposeAsync();
        }
    }

    [Fact]
    public async Task Guild_host_readiness_installs_real_fence_and_refuses_unbound_native_writers()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        var started = await readiness.EnsureReadyAsync(default);

        Assert.True(started.IsSuccess, started.Error.Message);

        Assert.True(readiness.RequireExecutionReady().IsSuccess);

        Assert.True(WorkspaceMutationFence.IsInstalled);

        Assert.False(WorkspaceMutationFence.TryBeginOpaqueEffect(out _));

        var writer = await WorkspaceMutationFence.AdmitAsync(f.WorkspaceRoot, [new("a.txt", RetroDownfall.Arcanum.Core.Guild.GuildPathScopeKind.File)], default);

        Assert.True(writer.IsFailure);

        f.Settings.CurrentValue.Features.Guild = false;

        Assert.True(readiness.RequireExecutionReady().IsFailure);

        Assert.True(WorkspaceMutationFence.IsInstalled);
    }

    [Fact]
    public async Task Guild_host_readiness_enable_after_dispatched_opaque_work_refuses_restart_required()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        f.Settings.CurrentValue.Features.Guild = false;

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        await readiness.StartAsync(default);

        Assert.False(WorkspaceMutationFence.IsInstalled);

        Assert.True(WorkspaceMutationFence.TryBeginOpaqueEffect(out var opaque));

        opaque!.MarkDispatched();

        opaque.Dispose();

        f.Settings.CurrentValue.Features.Guild = true;

        var enabled = await readiness.EnsureReadyAsync(default);

        Assert.True(enabled.IsFailure);

        Assert.Contains("restart", enabled.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(readiness.RequireExecutionReady().IsFailure);

        Assert.False(WorkspaceMutationFence.IsInstalled);
    }

    [Fact]
    public async Task Guild_host_readiness_clean_off_host_can_enable_and_reenable_same_fence()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        f.Settings.CurrentValue.Features.Guild = false;

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        await readiness.StartAsync(default);

        Assert.False(WorkspaceMutationFence.IsInstalled);

        f.Settings.CurrentValue.Features.Guild = true;

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        f.Settings.CurrentValue.Features.Guild = false;

        Assert.True(readiness.RequireExecutionReady().IsFailure);

        f.Settings.CurrentValue.Features.Guild = true;

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        Assert.True(readiness.RequireExecutionReady().IsSuccess);
    }

    [Fact]
    public async Task Guild_host_readiness_failed_identity_startup_never_installs_protection()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        await f.ExecuteAsync("DELETE FROM covenant_authority_state");

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        await readiness.StartAsync(default);

        Assert.True(readiness.RequireExecutionReady().IsFailure);

        Assert.False(WorkspaceMutationFence.IsInstalled);

        Assert.Equal(Guid.Empty, f.Policy.OperatorPrincipalId);
    }

    [Fact]
    public async Task Guild_host_readiness_failed_revalidation_revokes_previously_published_readiness()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        await f.ExecuteAsync("DELETE FROM covenant_authority_state");

        Assert.True((await readiness.EnsureReadyAsync(default)).IsFailure);

        Assert.True(readiness.RequireExecutionReady().IsFailure);

        Assert.True(WorkspaceMutationFence.IsInstalled);
    }

    [Fact]
    public async Task Guild_host_readiness_requires_its_retained_lock_and_retires_without_unprotecting_history()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        string path = Path.Combine(f.WorkspaceRoot, "host.lock");

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, path, f.Gate);

        using (var other = GuildWorkspaceLeaseHostLock.TryAcquire(path).Value)
        {
            Assert.True((await readiness.EnsureReadyAsync(default)).IsFailure);

            Assert.False(WorkspaceMutationFence.IsInstalled);
        }

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        Assert.NotEqual(Guid.Empty, f.Policy.InstallationGeneration);

        Assert.NotEqual(Guid.Empty, f.Policy.OperatorPrincipalId);

        string displaced = path + ".old";

        if (!OperatingSystem.IsWindows())
        {
            File.Move(path, displaced);

            File.WriteAllText(path, "replacement");

            Assert.True(readiness.RequireExecutionReady().IsFailure);

            Assert.True((await readiness.EnsureReadyAsync(default)).IsFailure);
        }
        else
        {
            Assert.Throws<IOException>(() => File.Move(path, displaced));

            Assert.True(readiness.RequireExecutionReady().IsSuccess);
        }

        await readiness.DisposeAsync();

        Assert.True(WorkspaceMutationFence.IsInstalled);

        Assert.False(WorkspaceMutationFence.TryBeginOpaqueEffect(out _));

        Assert.True((await readiness.EnsureReadyAsync(default)).IsFailure);
    }
}
