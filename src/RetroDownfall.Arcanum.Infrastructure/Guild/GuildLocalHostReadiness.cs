using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal interface IGuildLocalHostReadiness
{
    Result RequireExecutionReady();

    Task<Result> EnsureReadyAsync(CancellationToken cancellationToken);

    Result<IGuildWorkspaceLeaseService> GetWorkspaceLeaseService() => GuildLocalHostReadiness.Unavailable;
}

/// <summary>Host-lifetime ownership of the existing process-wide physical mutation fence.</summary>
internal sealed class GuildLocalHostReadiness(IOptionsMonitor<ArcanumSettings> settings,
    IGrimoireOrdinaryConnectionFactory connections, GuildLocalHostPolicy policy, SignetAuthority authority,
    TimeProvider time, string hostLockPath, IGrimoireConnectionAdmissionGate admission) : IGuildLocalHostReadiness, IHostedService, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Installation? _installed;

    private volatile bool _stopping;

    private volatile bool _identityReady;

    internal static Error Unavailable => new(ErrorCodes.Guild.Unavailable,
        "Guild workspace protection is unavailable; restart in Guild mode after reconciling opaque process history.");

    public Result RequireExecutionReady()
    {
        Installation? installed = Volatile.Read(ref _installed);

        return !_stopping && _identityReady && settings.CurrentValue.Features.Guild && installed is not null
            && installed.Generation == policy.InstallationGeneration && installed.Principal == policy.OperatorPrincipalId
            && installed.HostLock.Validate().IsSuccess
            ? Result.Success() : Unavailable;
    }

    public Result<IGuildWorkspaceLeaseService> GetWorkspaceLeaseService()
    {
        Installation? installed = Volatile.Read(ref _installed);

        return !_stopping && _identityReady && installed is not null
            && installed.Generation == policy.InstallationGeneration && installed.Principal == policy.OperatorPrincipalId
            && installed.HostLock.Validate().IsSuccess
            ? Result<IGuildWorkspaceLeaseService>.Success(installed.Broker) : Unavailable;
    }

    public async Task<Result> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_stopping || !settings.CurrentValue.Features.Guild) return Unavailable;

            Result identity = await policy.InitializeIdentityAsync(cancellationToken).ConfigureAwait(false);

            _identityReady = identity.IsSuccess;

            if (identity.IsFailure) return identity;

            if (!admission.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquiredWork)
                || acquiredWork is null) return Unavailable;

            await using IGrimoireWorkLease work = acquiredWork;

            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, work.MaintenanceRevocation);

            cancellationToken = lifetime.Token;

            if (_installed is not null) return RequireExecutionReady();

            if (!work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? acquiredEffect)
                || acquiredEffect is null) return Unavailable;

            await using IGrimoireExternalEffectGroup effect = acquiredEffect;

            cancellationToken.ThrowIfCancellationRequested();

            Result<GuildWorkspaceLeaseHostLock> acquired = GuildWorkspaceLeaseHostLock.TryAcquire(hostLockPath);

            if (acquired.IsFailure) return acquired.Error;

            GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

            GuildWorkspaceLeaseBroker broker = new(new GuildWorkspaceLeaseStore(connections), new WorkspaceLeaseIdentityResolver(),
                new GuildWorkspaceLeaseSignetPolicy(authority, policy), time, hostLock);

            WorkspaceMutationFence.Registration registration;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                registration = WorkspaceMutationFence.Install(new GuildWorkspaceMutationFence(broker));
            }
            catch (InvalidOperationException)
            {
                await broker.DisposeAsync().ConfigureAwait(false);

                return Unavailable;
            }
            catch
            {
                await broker.DisposeAsync().ConfigureAwait(false);

                throw;
            }

            Volatile.Write(ref _installed, new Installation(policy.InstallationGeneration, policy.OperatorPrincipalId,
                hostLock, broker, registration));

            return RequireExecutionReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken) => _ = await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

    public Task StopAsync(CancellationToken cancellationToken) => DisposeAsync().AsTask();

    public async ValueTask DisposeAsync()
    {
        _stopping = true;

        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            Installation? installed = Interlocked.Exchange(ref _installed, null);

            if (installed is null) return;

            installed.Registration.Dispose();

            await installed.Broker.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record Installation(Guid Generation, Guid Principal, GuildWorkspaceLeaseHostLock HostLock,
        GuildWorkspaceLeaseBroker Broker, WorkspaceMutationFence.Registration Registration);
}
