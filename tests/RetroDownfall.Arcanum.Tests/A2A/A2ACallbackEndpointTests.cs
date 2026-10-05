using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.A2A;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.A2A;

/// <summary>
/// The anonymous peer callback answers 404 to a caller it cannot authenticate and 503 to a peer whose
/// token it matched but whose Sending it could not settle.
/// </summary>
/// <remarks>
/// A peer treats 404 as terminal and stops retrying, so reporting a ledger fault as 404 told a legitimate
/// peer to give up on a Sending that was still owed its settlement. The route is anonymous, so the answer
/// must never differ between an unknown config id and a wrong token; it differs only for a ledger that
/// cannot answer, which says nothing about whether any id or token was right.
/// </remarks>
public sealed class A2ACallbackEndpointTests
{
    private const string ConfigId = "cfg-0123456789abcdef";

    private static readonly A2ASendingLedgerEntry Entry = new(Guid.NewGuid(), "owner");

    [Fact]
    public async Task SettleFromLedger_when_the_ledger_throws_after_a_matching_token_returns_503_not_404()
    {
        string token = A2ACallbackToken.Mint();

        FakeLedger ledger = new(
            new A2AOutboundCallback("task-1", "https://peer.example", A2ACallbackToken.Hash(token), Entry))
        {
            SettleFault = new InvalidOperationException("ledger unavailable"),
        };

        (IResult result, string body) = await SettleAsync(ledger, token);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf(result));

        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task SettleFromLedger_when_the_lookup_throws_returns_503_not_404()
    {
        FakeLedger ledger = new(callback: null)
        {
            FindFault = new InvalidOperationException("ledger unavailable"),
        };

        (IResult result, string body) = await SettleAsync(ledger, A2ACallbackToken.Mint());

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf(result));

        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task SettleFromLedger_lets_a_maintenance_refusal_reach_the_exception_handler()
    {
        string token = A2ACallbackToken.Mint();

        FakeLedger ledger = new(
            new A2AOutboundCallback("task-1", "https://peer.example", A2ACallbackToken.Hash(token), Entry))
        {
            SettleFault = new GrimoireMaintenanceUnavailableException(),
        };

        _ = await Assert.ThrowsAsync<GrimoireMaintenanceUnavailableException>(() => SettleAsync(ledger, token));
    }

    [Fact]
    public async Task SettleFromLedger_with_the_wrong_token_is_a_404_and_settles_nothing()
    {
        FakeLedger ledger = new(
            new A2AOutboundCallback("task-1", "https://peer.example", A2ACallbackToken.Hash(A2ACallbackToken.Mint()), Entry));

        (IResult result, _) = await SettleAsync(ledger, A2ACallbackToken.Mint());

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));

        Assert.Equal(0, ledger.Settled);
    }

    [Fact]
    public async Task SettleFromLedger_for_an_unknown_config_is_a_404()
    {
        FakeLedger ledger = new(callback: null);

        (IResult result, _) = await SettleAsync(ledger, A2ACallbackToken.Mint());

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
    }

    [Fact]
    public async Task SettleFromLedger_with_the_matching_token_settles_and_accepts()
    {
        string token = A2ACallbackToken.Mint();

        FakeLedger ledger = new(
            new A2AOutboundCallback("task-1", "https://peer.example", A2ACallbackToken.Hash(token), Entry));

        (IResult result, _) = await SettleAsync(ledger, token);

        Assert.Equal(StatusCodes.Status202Accepted, StatusOf(result));

        Assert.Equal(1, ledger.Settled);
    }

    private static async Task<(IResult Result, string Body)> SettleAsync(FakeLedger ledger, string token)
    {
        ServiceCollection services = new();

        services.AddLogging();

        services.AddSingleton<IA2ASendingLedger>(ledger);

        await using ServiceProvider provider = services.BuildServiceProvider();

        IResult result = await A2ACallbackEndpoints.SettleFromLedgerAsync(
            ConfigId,
            token,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            CancellationToken.None);

        DefaultHttpContext context = new() { RequestServices = provider };

        MemoryStream body = new();

        context.Response.Body = body;

        await result.ExecuteAsync(context);

        return (result, System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }

    private static int StatusOf(IResult result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode
        ?? throw new InvalidOperationException("The result carries no status code.");

    private sealed class FakeLedger(A2AOutboundCallback? callback) : IA2ASendingLedger
    {
        public Exception? FindFault { get; init; }

        public Exception? SettleFault { get; init; }

        public int Settled { get; private set; }

        public Task<A2AOutboundCallback?> FindOutboundCallbackAsync(
            string callbackConfigId,
            CancellationToken cancellationToken = default) =>
            FindFault is not null
                ? Task.FromException<A2AOutboundCallback?>(FindFault)
                : Task.FromResult(callback);

        public Task SettleOutboundAsync(
            A2ASendingLedgerEntry entry,
            A2ARemoteCost cost,
            CancellationToken cancellationToken = default)
        {
            if (SettleFault is not null)
            {
                return Task.FromException(SettleFault);
            }

            Settled++;

            return Task.CompletedTask;
        }

        public Task<A2ASendingLedgerEntry> RegisterInboundAsync(
            string taskId,
            Guid apprenticeId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<A2ASendingLedgerEntry> RegisterOutboundAsync(
            string remoteTaskId,
            string agentUrl,
            Guid? budgetReservationId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ReleaseAsync(A2ASendingLedgerEntry entry, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task MarkParkedAsync(
            A2ASendingLedgerEntry entry,
            string? contextId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<A2AParkedSending?> FindParkedInboundAsync(
            string taskId,
            bool takeLease = true,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Guid?> FindInboundApprenticeAsync(string taskId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RecordOutboundCallbackAsync(
            A2ASendingLedgerEntry entry,
            string callbackConfigId,
            string callbackTokenHash,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<A2ASendingLedgerEntry> FindOpenOutboundAsync(
            string remoteTaskId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
