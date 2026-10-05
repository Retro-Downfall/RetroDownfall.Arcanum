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

    /// <summary>
    /// A sink that throws instead of returning a failed result used to unwind the loop, so every later
    /// sink was skipped and the caller got an exception for an alert one sink could still have carried.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_continues_to_later_sinks_when_one_throws()
    {
        RecordingDispatcher throwing = new((_, _) =>
            throw new InvalidOperationException("secret-bearing detail"));

        RecordingDispatcher delivered = RecordingDispatcher.Returning(
            Result<CommLinkDeliveryResult>.Success(
                new CommLinkDeliveryResult(CommLinkDeliveryStatus.Delivered)));

        CapturingLogger logger = new();

        CommLinkMultiplexer multiplexer = new([throwing, delivered], logger);

        Result<CommLinkDeliveryResult> result = await multiplexer.DispatchAsync(Message);

        Assert.True(result.IsSuccess);

        Assert.Equal(CommLinkDeliveryStatus.Delivered, result.Value.Status);

        Assert.Single(throwing.Calls);

        Assert.Single(delivered.Calls);

        // The failure type is logged; the exception message is not, because a sink's message can carry
        // the secret-bearing endpoint it was talking to.
        string warning = Assert.Single(logger.Warnings);

        Assert.Contains(nameof(InvalidOperationException), warning, StringComparison.Ordinal);

        Assert.DoesNotContain("secret-bearing detail", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchAsync_reports_a_failure_when_the_only_sink_throws()
    {
        CommLinkMultiplexer multiplexer = new(
        [
            new RecordingDispatcher((_, _) => throw new InvalidOperationException("boom")),
        ]);

        Result<CommLinkDeliveryResult> result = await multiplexer.DispatchAsync(Message);

        Assert.True(result.IsFailure);

        Assert.Equal("CommLink.DispatcherException", result.Error.Code);

        Assert.DoesNotContain("boom", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchAsync_propagates_cancellation_without_trying_later_sinks()
    {
        using CancellationTokenSource cancellation = new();

        RecordingDispatcher cancelling = new((_, token) =>
        {
            cancellation.Cancel();

            token.ThrowIfCancellationRequested();

            return Task.FromResult(
                Result<CommLinkDeliveryResult>.Success(
                    new CommLinkDeliveryResult(CommLinkDeliveryStatus.Suppressed)));
        });

        RecordingDispatcher later = RecordingDispatcher.Returning(
            Result<CommLinkDeliveryResult>.Success(
                new CommLinkDeliveryResult(CommLinkDeliveryStatus.Delivered)));

        CommLinkMultiplexer multiplexer = new([cancelling, later]);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => multiplexer.DispatchAsync(Message, cancellation.Token));

        Assert.Empty(later.Calls);
    }

    /// <summary>
    /// An earlier sink already carried the alert, so a caller that cancels afterwards cannot un-send it.
    /// The delivered verdict is the true one: an <see cref="OperationCanceledException"/> here would tell
    /// the caller nothing was sent, and a retry would deliver the alert to that sink twice. Later sinks
    /// are still not tried once the caller has cancelled.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_reports_delivered_when_the_caller_cancels_after_a_sink_delivered()
    {
        using CancellationTokenSource cancellation = new();

        RecordingDispatcher delivered = RecordingDispatcher.Returning(
            Result<CommLinkDeliveryResult>.Success(
                new CommLinkDeliveryResult(CommLinkDeliveryStatus.Delivered)));

        RecordingDispatcher cancelling = new((_, token) =>
        {
            cancellation.Cancel();

            token.ThrowIfCancellationRequested();

            return Task.FromResult(
                Result<CommLinkDeliveryResult>.Success(
                    new CommLinkDeliveryResult(CommLinkDeliveryStatus.Suppressed)));
        });

        RecordingDispatcher later = RecordingDispatcher.Returning(
            Result<CommLinkDeliveryResult>.Success(
                new CommLinkDeliveryResult(CommLinkDeliveryStatus.Delivered)));

        CommLinkMultiplexer multiplexer = new([delivered, cancelling, later]);

        Result<CommLinkDeliveryResult> result = await multiplexer.DispatchAsync(Message, cancellation.Token);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(CommLinkDeliveryStatus.Delivered, result.Value.Status);

        Assert.Single(delivered.Calls);

        Assert.Single(cancelling.Calls);

        Assert.Empty(later.Calls);
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
