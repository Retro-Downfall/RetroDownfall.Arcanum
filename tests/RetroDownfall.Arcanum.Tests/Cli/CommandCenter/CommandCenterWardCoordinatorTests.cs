using RetroDownfall.Arcanum.Cli.CommandCenter;
using Xunit;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterWardCoordinatorTests
{
    [Fact]
    public async Task RequestApprovalAsync_Completes_When_TryCompletePending()
    {
        CommandCenterWardCoordinator coordinator = new();
        bool shown = false;
        coordinator.SetUiCallbacks(_ => shown = true, () => { });

        Task<WardApprovalDecision> pending = coordinator.RequestApprovalAsync(
            new WardApprovalRequest("ward-1", "execute_command", "{\"command\":\"dotnet --version\"}"),
            CancellationToken.None);

        await Task.Yield();
        Assert.True(shown);
        Assert.NotNull(coordinator.PendingRequest);
        Assert.True(coordinator.TryCompletePending(WardApprovalDecision.Allow));

        Assert.Equal(WardApprovalDecision.Allow, await pending);
        Assert.Null(coordinator.PendingRequest);
    }

    [Fact]
    public async Task RequestApprovalAsync_DeniesPrevious_WhenReplaced()
    {
        CommandCenterWardCoordinator coordinator = new();
        coordinator.SetUiCallbacks(_ => { }, () => { });

        Task<WardApprovalDecision> first = coordinator.RequestApprovalAsync(
            new WardApprovalRequest("ward-1", "execute_command", "{}"),
            CancellationToken.None);

        await Task.Yield();

        Task<WardApprovalDecision> second = coordinator.RequestApprovalAsync(
            new WardApprovalRequest("ward-2", "write_file", "{}"),
            CancellationToken.None);

        Assert.Equal(WardApprovalDecision.Deny, await first);

        Assert.True(coordinator.TryCompletePending(WardApprovalDecision.Allow));
        Assert.Equal(WardApprovalDecision.Allow, await second);
    }

    [Fact]
    public async Task TryResolvePendingWardAsDenied_CompletesPending_AndIsIdempotent()
    {
        CommandCenterWardCoordinator coordinator = new();
        coordinator.SetUiCallbacks(_ => { }, () => { });

        Task<WardApprovalDecision> pending = coordinator.RequestApprovalAsync(
            new WardApprovalRequest("ward-1", "execute_command", "{}"),
            CancellationToken.None);

        await Task.Yield();

        Assert.True(coordinator.TryResolvePendingWardAsDenied());
        Assert.False(coordinator.TryResolvePendingWardAsDenied());

        Assert.Equal(WardApprovalDecision.Deny, await pending);
    }

    [Fact]
    public void TryResolvePendingWardAsDenied_ReturnsFalse_WhenNothingPending()
    {
        CommandCenterWardCoordinator coordinator = new();
        Assert.False(coordinator.TryResolvePendingWardAsDenied());
    }

    [Fact]
    public void FormatArgumentsPreview_Truncates()
    {
        string payload = "{\"command\":\"" + new string('x', 300) + "\"}";
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        string preview = CommandCenterWardCoordinator.FormatArgumentsPreview(doc.RootElement, maxChars: 40);
        Assert.True(preview.Length <= 41);
        Assert.EndsWith("…", preview);
    }
}
