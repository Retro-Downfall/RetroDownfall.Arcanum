using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Ux.Services.Services;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class FoundryFloorViewModelTests
{

    [Fact]
    public void AppendLine_EnforcesLineCapAndKeepsTheNewestLines()
    {

        FoundryFloorViewModel viewModel = new(new NullLogService());

        for (int i = 0; i < FoundryFloorViewModel.MaxLines + 50; i++)
        {

            viewModel.AppendLine($"line-{i}");

        }

        Assert.Equal(FoundryFloorViewModel.MaxLines, viewModel.Lines.Count);

        Assert.Equal($"line-{FoundryFloorViewModel.MaxLines + 49}", viewModel.Lines[^1]);

        Assert.Equal("line-50", viewModel.Lines[0]);

        viewModel.Dispose();

    }

    [Fact]
    public void Clear_EmptiesTheBufferAndRestoresTheEmptyState()
    {

        FoundryFloorViewModel viewModel = new(new NullLogService());

        viewModel.AppendLine("first");

        viewModel.AppendLine("second");

        Assert.False(viewModel.HasNoLines);

        viewModel.ClearCommand.Execute(null);

        Assert.Empty(viewModel.Lines);

        Assert.True(viewModel.HasNoLines);

        Assert.Equal(string.Empty, viewModel.LatestLine);

        viewModel.Dispose();

    }

    [Fact]
    public void LogStream_EndingWithoutCancellation_AppendsATerminalNotice()
    {

        FoundryFloorViewModel viewModel = new(new TwoFrameThenCompleteLogService());

        viewModel.IsVisible = true;

        Assert.Contains("Log stream ended.", viewModel.Lines);

        viewModel.Dispose();

    }

    private sealed class TwoFrameThenCompleteLogService : ILogService
    {

        public Task<ApiResponse<LogQueryResult>?> QueryAsync(
            LogLevel? minLevel,
            string? category,
            string? search,
            int? limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<ApiResponse<LogQueryResult>?>(null);

        public async IAsyncEnumerable<LogEntry> StreamLogsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {

            await Task.CompletedTask;

            yield return NewEntry("first");

            yield return NewEntry("second");

        }

        private static LogEntry NewEntry(string message) => new(
            Sequence: 0,
            Timestamp: DateTimeOffset.UnixEpoch,
            Level: LogLevel.Information,
            Category: "test",
            Message: message,
            Exception: null,
            CorrelationId: null,
            TraceId: null,
            Properties: []);

    }

}
