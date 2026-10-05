using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The outbound half of the durable A2A ledger, kept honestly enough that "one row per Sending" and "the
/// row was closed" are claims a test can actually check. When a <see cref="A2ASendingLeaseRenewer"/> is
/// supplied it is tracked and forgotten the way the real ledger does, so "no longer renewing" is
/// observable.
/// </summary>
internal sealed class OutboundSendingLedgerFake(A2ASendingLeaseRenewer? renewer = null) : IA2ASendingLedger
{
    private readonly Dictionary<Guid, string> _open = [];

    public List<string> Registered { get; } = [];

    public List<string> Settled { get; } = [];

    public List<string> Released { get; } = [];

    public List<A2ASendingLedgerEntry> Entries { get; } = [];

    public IReadOnlyCollection<Guid> OpenEntries => _open.Keys;

    /// <summary>
    /// A scope factory whose scopes resolve this ledger and, when given, the renewer — the same two
    /// services <c>A2AClientService</c> reaches through its singleton-safe scope.
    /// </summary>
    public static IServiceScopeFactory ScopeFactoryFor(IA2ASendingLedger ledger, A2ASendingLeaseRenewer? renewer = null)
    {
        ServiceCollection services = new();

        services.AddSingleton(ledger);

        if (renewer is not null)
        {
            services.AddSingleton(renewer);
        }

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>A renewer over a gate nothing will ever close, suitable for asserting what it holds.</summary>
    public static A2ASendingLeaseRenewer CreateRenewer() =>
        new(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new GrimoireConnectionAdmissionGate(TimeProvider.System),
            TimeProvider.System,
            NullLogger<A2ASendingLeaseRenewer>.Instance);

    public Task<A2ASendingLedgerEntry> RegisterOutboundAsync(
        string remoteTaskId,
        string agentUrl,
        Guid? budgetReservationId = null,
        CancellationToken cancellationToken = default)
    {
        Registered.Add(remoteTaskId);

        A2ASendingLedgerEntry entry = new(Guid.NewGuid(), "test");

        _open[entry.OperationId] = remoteTaskId;

        Entries.Add(entry);

        renewer?.Track(entry);

        return Task.FromResult(entry);
    }

    public Task SettleOutboundAsync(
        A2ASendingLedgerEntry entry,
        A2ARemoteCost cost,
        CancellationToken cancellationToken = default)
    {
        if (_open.Remove(entry.OperationId, out string? taskId))
        {
            Settled.Add(taskId);
        }

        renewer?.Forget(entry);

        return Task.CompletedTask;
    }

    public Task ReleaseAsync(A2ASendingLedgerEntry entry, CancellationToken cancellationToken = default)
    {
        if (_open.Remove(entry.OperationId, out string? taskId))
        {
            Released.Add(taskId);
        }

        renewer?.Forget(entry);

        return Task.CompletedTask;
    }

    public Task<A2ASendingLedgerEntry> RegisterInboundAsync(
        string taskId,
        Guid apprenticeId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new A2ASendingLedgerEntry(Guid.NewGuid(), "test"));

    public Task MarkParkedAsync(
        A2ASendingLedgerEntry entry,
        string? contextId,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<A2AParkedSending?> FindParkedInboundAsync(
        string taskId,
        bool takeLease = true,
        CancellationToken cancellationToken = default) => Task.FromResult<A2AParkedSending?>(null);

    public Task<Guid?> FindInboundApprenticeAsync(string taskId, CancellationToken cancellationToken = default) =>
        Task.FromResult<Guid?>(null);

    public Task RecordOutboundCallbackAsync(
        A2ASendingLedgerEntry entry,
        string callbackConfigId,
        string callbackTokenHash,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<A2AOutboundCallback?> FindOutboundCallbackAsync(
        string callbackConfigId,
        CancellationToken cancellationToken = default) => Task.FromResult<A2AOutboundCallback?>(null);

    public Task<A2ASendingLedgerEntry> FindOpenOutboundAsync(
        string remoteTaskId,
        CancellationToken cancellationToken = default)
    {
        foreach ((Guid operationId, string taskId) in _open)
        {
            if (string.Equals(taskId, remoteTaskId, StringComparison.Ordinal))
            {
                return Task.FromResult(new A2ASendingLedgerEntry(operationId, "test"));
            }
        }

        return Task.FromResult<A2ASendingLedgerEntry>(default);
    }
}
