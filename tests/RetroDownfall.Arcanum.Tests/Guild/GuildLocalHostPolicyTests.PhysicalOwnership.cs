using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Physical_ownership_pins_retain_the_real_lock_until_the_last_dispose_after_stop()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        string path = Path.Combine(f.WorkspaceRoot, "host.lock");

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, path, f.Gate);

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        var first = readiness.RetainPhysicalWorkOwnership();

        var second = readiness.RetainPhysicalWorkOwnership();

        Assert.True(first.IsSuccess);

        Assert.True(second.IsSuccess);

        f.Settings.CurrentValue.Features.Guild = false;

        Assert.True(readiness.RetainPhysicalWorkOwnership().IsFailure);

        Assert.True(GuildWorkspaceLeaseHostLock.TryAcquire(path).IsFailure);

        await readiness.DisposeAsync();

        Assert.True(readiness.RetainPhysicalWorkOwnership().IsFailure);

        Assert.True(GuildWorkspaceLeaseHostLock.TryAcquire(path).IsFailure);

        first.Value.Dispose();

        first.Value.Dispose();

        Assert.True(GuildWorkspaceLeaseHostLock.TryAcquire(path).IsFailure);

        second.Value.Dispose();

        second.Value.Dispose();

        var successor = GuildWorkspaceLeaseHostLock.TryAcquire(path);

        Assert.True(successor.IsSuccess, successor.Error.Message);

        successor.Value.Dispose();
    }

    [Fact]
    public async Task Physical_ownership_requires_initialized_enabled_and_revalidated_identity()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        Assert.True(readiness.RetainPhysicalWorkOwnership().IsFailure);

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        f.Settings.CurrentValue.Features.Guild = false;

        Assert.True(readiness.RetainPhysicalWorkOwnership().IsFailure);

        f.Settings.CurrentValue.Features.Guild = true;

        await f.ExecuteAsync("DELETE FROM covenant_authority_state");

        Assert.True((await readiness.EnsureReadyAsync(default)).IsFailure);

        Assert.True(readiness.RetainPhysicalWorkOwnership().IsFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Physical_ownership_refuses_replaced_lock_identity(bool symbolicLink)
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        string path = Path.Combine(f.WorkspaceRoot, "host.lock");

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, path, f.Gate);

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() => File.Move(path, path + ".old"));

            var pin = readiness.RetainPhysicalWorkOwnership();

            Assert.True(pin.IsSuccess);

            pin.Value.Dispose();
        }
        else
        {
            File.Move(path, path + ".old");

            if (symbolicLink) File.CreateSymbolicLink(path, path + ".old");
            else File.WriteAllText(path, "replacement");

            Assert.True(readiness.RetainPhysicalWorkOwnership().IsFailure);
        }
    }
    [Theory]
    [InlineData("generation")]
    [InlineData("principal")]
    public async Task Physical_ownership_refuses_a_failed_identity_revalidation(string changed)
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        if (changed == "generation")
            await f.ExecuteAsync($"UPDATE covenant_authority_state SET InstallationIdentity='{Guid.NewGuid():D}'");
        else
        {
            await f.ExecuteAsync("DELETE FROM guild_workspace_authority_state");

            await f.ExecuteAsync($"INSERT INTO guild_workspace_authority_state VALUES(1,'{f.Generation.ToString("D").ToUpperInvariant()}','{Guid.NewGuid().ToString("D").ToUpperInvariant()}')");
        }

        Assert.True((await readiness.EnsureReadyAsync(default)).IsFailure);

        Assert.True(readiness.RetainPhysicalWorkOwnership().IsFailure);
    }

    [Fact]
    public async Task Physical_ownership_refuses_shutdown_during_pin_revalidation()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        string path = Path.Combine(f.WorkspaceRoot, "host.lock");

        using ManualResetEventSlim entered = new(false);

        using ManualResetEventSlim proceed = new(false);

        var settings = new PhysicalOwnershipOptions(f.Settings.CurrentValue, entered, proceed);

        await using GuildLocalHostReadiness readiness = new(settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, path, f.Gate);

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        settings.Arm(2);

        Task<RetroDownfall.Arcanum.Core.Primitives.Result<IDisposable>> acquire = Task.Run(readiness.RetainPhysicalWorkOwnership);

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

            Task stop = readiness.DisposeAsync().AsTask();

            proceed.Set();

            var result = await acquire.WaitAsync(TimeSpan.FromSeconds(10));

            if (result.IsSuccess) result.Value.Dispose();

            await stop.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(result.IsFailure);

            var successor = GuildWorkspaceLeaseHostLock.TryAcquire(path);

            Assert.True(successor.IsSuccess, successor.Error.Message);

            successor.Value.Dispose();
        }
        finally
        {
            proceed.Set();
        }
    }

    [Fact]
    public async Task Physical_ownership_refuses_while_identity_revalidation_is_in_progress()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        using ManualResetEventSlim entered = new(false);

        using ManualResetEventSlim proceed = new(false);

        var settings = new PhysicalOwnershipOptions(f.Settings.CurrentValue, entered, proceed);

        await using GuildLocalHostReadiness readiness = new(settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        await f.ExecuteAsync($"UPDATE covenant_authority_state SET InstallationIdentity='{Guid.NewGuid():D}'");

        settings.Arm(1);

        Task<RetroDownfall.Arcanum.Core.Primitives.Result> revalidate = Task.Run(() => readiness.EnsureReadyAsync(default));

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

            Assert.True(readiness.RetainPhysicalWorkOwnership().IsFailure);
        }
        finally
        {
            proceed.Set();
        }

        Assert.True((await revalidate.WaitAsync(TimeSpan.FromSeconds(10))).IsFailure);

        Assert.True(readiness.RetainPhysicalWorkOwnership().IsFailure);
    }

    [Fact]
    public void Physical_ownership_interface_default_fails_closed()
    {
        IGuildLocalHostReadiness readiness = new TestGuildLocalHostReadiness();

        Assert.True(readiness.RetainPhysicalWorkOwnership().IsFailure);
    }

    private sealed class PhysicalOwnershipOptions(ArcanumSettings value, ManualResetEventSlim entered,
        ManualResetEventSlim proceed) : IOptionsMonitor<ArcanumSettings>
    {
        private int _reads;

        private int _blockAt;

        internal void Arm(int blockAt) => _blockAt = blockAt;

        public ArcanumSettings CurrentValue
        {
            get
            {
                if (_blockAt != 0 && Interlocked.Increment(ref _reads) == _blockAt)
                {
                    entered.Set();

                    if (!proceed.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                }

                return value;
            }
        }

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;
    }

}
