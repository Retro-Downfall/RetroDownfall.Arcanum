using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

[Collection("WorkspacePathPolicy")]
public sealed class GuildWorkspaceLeaseHostLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "guild-host-lock-" + Guid.NewGuid().ToString("N"));

    private string LockPath => Path.Combine(_root, "control", "broker.lock");

    [Fact]
    public void Only_one_host_holds_the_installation_lock_and_new_acquisition_gets_a_new_identity()
    {
        var first = GuildWorkspaceLeaseHostLock.TryAcquire(LockPath);

        Assert.True(first.IsSuccess, first.Error.ToString());

        Guid oldHost;

        using (first.Value)
        {
            oldHost = first.Value.HostInstanceId;

            Assert.NotEqual(Guid.Empty, oldHost);

            Assert.True(first.Value.Validate().IsSuccess);

            var second = GuildWorkspaceLeaseHostLock.TryAcquire(LockPath);

            if (second.IsSuccess)
            {
                second.Value.Dispose();
            }

            Assert.True(second.IsFailure);
        }

        var replacement = GuildWorkspaceLeaseHostLock.TryAcquire(LockPath);

        Assert.True(replacement.IsSuccess, replacement.Error.ToString());

        using (replacement.Value)
        {
            Assert.NotEqual(oldHost, replacement.Value.HostInstanceId);
        }
    }

    [Fact]
    public void Closing_stops_new_admission_but_retains_the_os_lock_until_every_pin_drains()
    {
        var result = GuildWorkspaceLeaseHostLock.TryAcquire(LockPath);

        Assert.True(result.IsSuccess, result.Error.ToString());

        using var host = result.Value;

        var first = host.Pin();

        var second = host.Pin();

        Assert.True(first.IsSuccess);

        Assert.True(second.IsSuccess);

        using var firstPin = first.Value;

        using var secondPin = second.Value;

        host.Dispose();

        Assert.True(host.Pin().IsFailure);

        Assert.True(host.Validate().IsFailure);

        AssertUnavailable();

        firstPin.Dispose();

        firstPin.Dispose();

        AssertUnavailable();

        secondPin.Dispose();

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(LockPath);

        Assert.True(acquired.IsSuccess, acquired.Error.ToString());

        acquired.Value.Dispose();
    }

    [SkippableFact]
    public void Replaced_lock_file_or_reparse_path_revokes_admission()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows no-delete sharing prevents this replacement while the lock is held.");

        var result = GuildWorkspaceLeaseHostLock.TryAcquire(LockPath);

        Assert.True(result.IsSuccess, result.Error.ToString());

        using var host = result.Value;

        File.Move(LockPath, LockPath + ".old");

        File.WriteAllText(LockPath, "replacement");

        Assert.True(host.Validate().IsFailure);

        Assert.True(host.Pin().IsFailure);

        File.Delete(LockPath);

        File.CreateSymbolicLink(LockPath, LockPath + ".old");

        Assert.True(host.Validate().IsFailure);
    }

    [Fact]
    public void Managed_host_stop_proof_expires_when_its_retained_lock_closes()
    {
        var result = GuildWorkspaceLeaseHostLock.TryAcquire(LockPath);

        Assert.True(result.IsSuccess, result.Error.ToString());

        using var host = result.Value;

        var proof = host.ProvePriorManagedHostStopped();

        Assert.True(proof.IsSuccess);

        Assert.Equal(host.HostInstanceId, proof.Value.HostInstanceId);

        Assert.True(proof.Value.Validate().IsSuccess);

        host.Dispose();

        Assert.True(proof.Value.Validate().IsFailure);

        Assert.True(host.ProvePriorManagedHostStopped().IsFailure);
    }

    [SkippableFact]
    public void Windows_retained_no_delete_sharing_prevents_lock_leaf_replacement()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows sharing semantics require the Windows lane.");

        var result = GuildWorkspaceLeaseHostLock.TryAcquire(LockPath);

        Assert.True(result.IsSuccess, result.Error.ToString());

        using var host = result.Value;

        Exception? replacement = Record.Exception(() => File.Move(LockPath, LockPath + ".old"));

        Assert.True(replacement is IOException or UnauthorizedAccessException);

        Assert.True(host.Validate().IsSuccess);
    }

    [Fact]
    public void Recovery_proof_pin_retains_exclusion_until_the_owning_transaction_has_drained()
    {
        var result = GuildWorkspaceLeaseHostLock.TryAcquire(LockPath);

        Assert.True(result.IsSuccess, result.Error.ToString());

        using var host = result.Value;

        var proof = host.ProvePriorManagedHostStopped();

        Assert.True(proof.IsSuccess);

        var pinned = proof.Value.Pin();

        Assert.True(pinned.IsSuccess);

        using var transactionPin = pinned.Value;

        host.Dispose();

        Assert.True(proof.Value.Pin().IsFailure);

        AssertUnavailable();

        transactionPin.Dispose();

        var successor = GuildWorkspaceLeaseHostLock.TryAcquire(LockPath);

        Assert.True(successor.IsSuccess, successor.Error.ToString());

        successor.Value.Dispose();
    }

    private void AssertUnavailable()
    {
        var result = GuildWorkspaceLeaseHostLock.TryAcquire(LockPath);

        if (result.IsSuccess)
        {
            result.Value.Dispose();
        }

        Assert.True(result.IsFailure);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
