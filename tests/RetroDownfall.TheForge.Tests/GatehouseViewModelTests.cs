using System.Text.Json;
using RetroDownfall.Arcanum.Core.Wards;
using RetroDownfall.TheForge.Ux.ViewModels.Gatehouse;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class GatehouseViewModelTests
{

    [Fact]
    public async Task RefreshAsync_UpdatesExistingWardCards()
    {

        FakeGatehouseDataSource dataSource = new()
        {
            Wards =
            [
                NewWard("ward-1", "hammer", """{"force":1}""", DateTimeOffset.UtcNow.AddMinutes(2)),
            ],
        };

        GatehouseViewModel viewModel = new(dataSource, new FakeWhispersService());

        await viewModel.RefreshAsync(CancellationToken.None);

        WardCardViewModel card = Assert.Single(viewModel.Wards);

        Assert.Equal("hammer", card.ToolName);

        dataSource.Wards =
        [
            NewWard("ward-1", "anvil", """{"force":9}""", DateTimeOffset.UtcNow.AddMinutes(5)),
        ];

        await viewModel.RefreshAsync(CancellationToken.None);

        WardCardViewModel updated = Assert.Single(viewModel.Wards);

        Assert.Same(card, updated);

        Assert.Equal("anvil", updated.ToolName);

        Assert.Contains("force", updated.ArgumentsSummary);

        Assert.Contains("9", updated.ArgumentsSummary);

        viewModel.Dispose();

    }

    [Fact]
    public async Task RefreshAsync_PopulatesWardCardsWithCountdown()
    {

        FakeGatehouseDataSource dataSource = new()
        {
            Wards =
            [
                NewWard("ward-1", "hammer", """{"force":1}""", DateTimeOffset.UtcNow.AddMinutes(2)),
            ],
        };

        GatehouseViewModel viewModel = new(dataSource, new FakeWhispersService());

        await viewModel.RefreshAsync(CancellationToken.None);

        WardCardViewModel card = Assert.Single(viewModel.Wards);

        Assert.Equal("hammer", card.ToolName);

        Assert.Contains("force", card.ArgumentsSummary);

        Assert.False(string.IsNullOrWhiteSpace(card.CountdownText));

        Assert.False(viewModel.HasNoWards);

        viewModel.Dispose();

    }

    [Fact]
    public async Task ApproveCommand_ResolvesAllowTrueAndRemovesCard()
    {

        FakeGatehouseDataSource dataSource = new()
        {
            Wards =
            [
                NewWard("ward-1", "hammer", "{}", DateTimeOffset.UtcNow.AddMinutes(1)),
            ],
        };

        GatehouseViewModel viewModel = new(dataSource, new FakeWhispersService());

        await viewModel.RefreshAsync(CancellationToken.None);

        await viewModel.Wards[0].ApproveCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Wards);

        Assert.Equal(("ward-1", true, null), dataSource.LastResolve);

        Assert.True(viewModel.HasNoWards);

        viewModel.Dispose();

    }

    [Fact]
    public async Task DenyCommand_ResolvesAllowFalseWithReason()
    {

        FakeGatehouseDataSource dataSource = new()
        {
            Wards =
            [
                NewWard("ward-2", "chisel", "{}", DateTimeOffset.UtcNow.AddMinutes(1)),
            ],
        };

        GatehouseViewModel viewModel = new(dataSource, new FakeWhispersService());

        await viewModel.RefreshAsync(CancellationToken.None);

        viewModel.Wards[0].DenyReason = "too sharp";

        await viewModel.Wards[0].DenyCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Wards);

        Assert.Equal(("ward-2", false, "too sharp"), dataSource.LastResolve);

        viewModel.Dispose();

    }

    [Fact]
    public void IsVisible_StartsAndStopsPollingWithoutThrowing()
    {

        FakeGatehouseDataSource dataSource = new();

        GatehouseViewModel viewModel = new(dataSource, new FakeWhispersService());

        viewModel.IsVisible = true;

        viewModel.IsVisible = false;

        viewModel.Dispose();

    }

    [Fact]
    public void ArgumentsSummary_IsFormattedOnceAndReusedAcrossReads()
    {

        WardCardViewModel card = new(
            NewWard("ward-1", "hammer", """{"force":1}""", DateTimeOffset.UtcNow.AddMinutes(2)),
            static (_, _, _, _) => Task.CompletedTask);

        string first = card.ArgumentsSummary;

        Assert.Same(first, card.ArgumentsSummary);

        Assert.Contains('\n', first);

        Assert.Contains("\"force\": 1", first, StringComparison.Ordinal);

        card.Update(NewWard("ward-1", "anvil", """{"force":9}""", DateTimeOffset.UtcNow.AddMinutes(5)));

        Assert.Contains("\"force\": 9", card.ArgumentsSummary, StringComparison.Ordinal);

    }

    [Fact]
    public void ArgumentsSummary_TruncatesLongArgumentPayloads()
    {

        string padded = new('a', 900);

        WardCardViewModel card = new(
            NewWard("ward-1", "hammer", $$"""{"note":"{{padded}}"}""", DateTimeOffset.UtcNow.AddMinutes(2)),
            static (_, _, _, _) => Task.CompletedTask);

        string summary = card.ArgumentsSummary;

        Assert.EndsWith("…", summary, StringComparison.Ordinal);

        Assert.True(summary.Length <= 401);

    }

    private static WardDto NewWard(string id, string tool, string argsJson, DateTimeOffset expiresAt)
    {

        using JsonDocument doc = JsonDocument.Parse(argsJson);

        return new WardDto(id, tool, doc.RootElement.Clone(), "session-1", DateTimeOffset.UtcNow, expiresAt);

    }

    private sealed class FakeGatehouseDataSource : IGatehouseDataSource
    {

        public IReadOnlyList<WardDto> Wards { get; set; } = [];

        public (string WardId, bool Allow, string? Reason)? LastResolve { get; private set; }

        public Task<IReadOnlyList<WardDto>> ListWardsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Wards);

        public Task<bool> ResolveAsync(string wardId, bool allow, string? reason, CancellationToken cancellationToken)
        {

            LastResolve = (wardId, allow, reason);

            return Task.FromResult(true);

        }

    }

}
