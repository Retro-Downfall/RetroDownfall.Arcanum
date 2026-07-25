using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.CommLink;

namespace RetroDownfall.Arcanum.Tests.CommLink;

public sealed class CommLinkMultiplexerTests
{

    private static readonly CommLinkMessage Message =
        new("Budget warning", "Daily spend is high.", CommLinkSeverity.Warning, "budget-monitor");

    [Fact]
    public async Task DispatchAsync_without_dispatchers_returns_suppressed()
    {

        CommLinkMultiplexer multiplexer = new([]);

        Result<CommLinkDeliveryResult> result = await multiplexer.DispatchAsync(Message);

        Assert.True(result.IsSuccess);

        Assert.Equal(CommLinkDeliveryStatus.Suppressed, result.Value.Status);

    }

    [Fact]
    public async Task DispatchAsync_when_all_dispatchers_suppress_forwards_message_and_token()
    {

        RecordingDispatcher first = RecordingDispatcher.Returning(
            Result<CommLinkDeliveryResult>.Success(
                new CommLinkDeliveryResult(CommLinkDeliveryStatus.Suppressed)));

        RecordingDispatcher second = RecordingDispatcher.Returning(
            Result<CommLinkDeliveryResult>.Success(
                new CommLinkDeliveryResult(CommLinkDeliveryStatus.Suppressed)));

        CommLinkMultiplexer multiplexer = new([first, second]);

        using CancellationTokenSource cancellation = new();

        Result<CommLinkDeliveryResult> result =
            await multiplexer.DispatchAsync(Message, cancellation.Token);

        Assert.True(result.IsSuccess);

        Assert.Equal(CommLinkDeliveryStatus.Suppressed, result.Value.Status);

        Assert.Collection(
            first.Calls,
            call =>
            {

                Assert.Equal(Message, call.Message);

                Assert.Equal(cancellation.Token, call.CancellationToken);

            });

        Assert.Collection(
            second.Calls,
            call =>
            {

                Assert.Equal(Message, call.Message);

                Assert.Equal(cancellation.Token, call.CancellationToken);

            });

    }

    [Fact]
    public async Task DispatchAsync_when_any_dispatcher_delivers_delivery_wins_and_failures_are_logged()
    {

        Error firstFailure = new("CommLink.First", "first failed");

        Error lastFailure = new("CommLink.Last", "last failed");

        RecordingDispatcher first = RecordingDispatcher.Returning(
            Result<CommLinkDeliveryResult>.Failure(firstFailure));

        RecordingDispatcher delivered = RecordingDispatcher.Returning(
            Result<CommLinkDeliveryResult>.Success(
                new CommLinkDeliveryResult(CommLinkDeliveryStatus.Delivered)));

        RecordingDispatcher last = RecordingDispatcher.Returning(
            Result<CommLinkDeliveryResult>.Failure(lastFailure));

        CapturingLogger logger = new();

        CommLinkMultiplexer multiplexer = new([first, delivered, last], logger);

        Result<CommLinkDeliveryResult> result = await multiplexer.DispatchAsync(Message);

        Assert.True(result.IsSuccess);

        Assert.Equal(CommLinkDeliveryStatus.Delivered, result.Value.Status);

        Assert.Single(first.Calls);

        Assert.Single(delivered.Calls);

        Assert.Single(last.Calls);

        Assert.Collection(
            logger.Warnings,
            warning =>
            {

                Assert.Contains(firstFailure.Code, warning, StringComparison.Ordinal);

                Assert.Contains(firstFailure.Message, warning, StringComparison.Ordinal);

            },
            warning =>
            {

                Assert.Contains(lastFailure.Code, warning, StringComparison.Ordinal);

                Assert.Contains(lastFailure.Message, warning, StringComparison.Ordinal);

            });

    }

    [Fact]
    public async Task DispatchAsync_without_delivery_returns_the_last_failure()
    {

        Error firstFailure = new("CommLink.First", "first failed");

        Error lastFailure = new("CommLink.Last", "last failed");

        CommLinkMultiplexer multiplexer = new(
        [
            RecordingDispatcher.Returning(
                Result<CommLinkDeliveryResult>.Success(
                    new CommLinkDeliveryResult(CommLinkDeliveryStatus.Suppressed))),
            RecordingDispatcher.Returning(Result<CommLinkDeliveryResult>.Failure(firstFailure)),
            RecordingDispatcher.Returning(Result<CommLinkDeliveryResult>.Failure(lastFailure)),
        ]);

        Result<CommLinkDeliveryResult> result = await multiplexer.DispatchAsync(Message);

        Assert.True(result.IsFailure);

        Assert.Equal(lastFailure, result.Error);

    }

    private sealed class RecordingDispatcher(
        Func<CommLinkMessage, CancellationToken, Task<Result<CommLinkDeliveryResult>>> dispatch)
        : ICommLinkDispatcher
    {

        public List<(CommLinkMessage Message, CancellationToken CancellationToken)> Calls { get; } = [];

        public static RecordingDispatcher Returning(Result<CommLinkDeliveryResult> result) =>
            new((_, _) => Task.FromResult(result));

        public Task<Result<CommLinkDeliveryResult>> DispatchAsync(
            CommLinkMessage message,
            CancellationToken cancellationToken = default)
        {

            Calls.Add((message, cancellationToken));

            return dispatch(message, cancellationToken);

        }

    }

    private sealed class CapturingLogger : ILogger<CommLinkMultiplexer>
    {

        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {

            if (logLevel == LogLevel.Warning)
            {

                Warnings.Add(formatter(state, exception));

            }

        }

    }

}
