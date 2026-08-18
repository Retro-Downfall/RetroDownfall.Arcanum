using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class ActiveCampaignServiceTests
{

    [Fact]
    public async Task SetActiveCampaignAsync_PostsChangedToTheUiThreadWhenRaisedOffIt()
    {

        RecordingUiThreadDispatcher dispatcher = new() { OnUiThread = false };

        ActiveCampaignService service = new(
            new NullSettingsStore(),
            new StaticOptionsMonitor(new TheForgeSettings()),
            dispatcher);

        int raised = 0;

        service.ActiveCampaignChanged += (_, _) => raised++;

        await service.SetActiveCampaignAsync(Campaign());

        Assert.Equal(0, raised);

        Assert.Single(dispatcher.Pending);

        dispatcher.DrainPending();

        Assert.Equal(1, raised);

    }

    [Fact]
    public void HydrateIfMatching_PostsChangedToTheUiThreadWhenRaisedOffIt()
    {

        RecordingUiThreadDispatcher dispatcher = new() { OnUiThread = false };

        CampaignDto campaign = Campaign();

        ActiveCampaignService service = new(
            new NullSettingsStore(),
            new StaticOptionsMonitor(new TheForgeSettings { LastCampaignId = campaign.Id }),
            dispatcher);

        int raised = 0;

        service.ActiveCampaignChanged += (_, _) => raised++;

        service.HydrateIfMatching(campaign);

        Assert.Equal(0, raised);

        Assert.Single(dispatcher.Pending);

        dispatcher.DrainPending();

        Assert.Equal(1, raised);

    }

    [Fact]
    public async Task SetActiveCampaignAsync_RaisesChangedInlineWhenAlreadyOnTheUiThread()
    {

        RecordingUiThreadDispatcher dispatcher = new() { OnUiThread = true };

        ActiveCampaignService service = new(
            new NullSettingsStore(),
            new StaticOptionsMonitor(new TheForgeSettings()),
            dispatcher);

        int raised = 0;

        service.ActiveCampaignChanged += (_, _) => raised++;

        await service.SetActiveCampaignAsync(Campaign());

        Assert.Equal(1, raised);

        Assert.Empty(dispatcher.Pending);

    }

    [Fact]
    public async Task SetActiveCampaignAsync_RaisesChangedEvenWhenTheSettingsWriteFails()
    {

        ThrowingSettingsStore store = new();

        ActiveCampaignService service = new(
            store,
            new StaticOptionsMonitor(new TheForgeSettings()),
            new RecordingUiThreadDispatcher { OnUiThread = true });

        int raised = 0;

        service.ActiveCampaignChanged += (_, _) => raised++;

        CampaignDto campaign = Campaign();

        _ = await Assert.ThrowsAsync<IOException>(() => service.SetActiveCampaignAsync(campaign));

        // The in-memory active campaign already changed under the gate, so subscribers must be told:
        // otherwise ActiveCampaign returns the new campaign while every bound surface renders the old one.
        Assert.Equal(campaign.Id, service.ActiveCampaign?.Id);

        Assert.Equal(1, raised);

    }

    private static CampaignDto Campaign() =>
        new(
            Guid.NewGuid(),
            "Sweep",
            "/campaigns/sweep",
            WorkspaceType.Campaign,
            null,
            CampaignSettings.CreateDefault(),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private sealed class NullSettingsStore : ITheForgeSettingsStore
    {

        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "forge-active-campaign-null.json");

        public Task<TheForgeSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TheForgeSettings());

        public Task SaveAsync(TheForgeSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SavePatchAsync(
            Func<TheForgeSettings, TheForgeSettings> patch,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

    }

    private sealed class ThrowingSettingsStore : ITheForgeSettingsStore
    {

        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "forge-active-campaign.json");

        public Task<TheForgeSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TheForgeSettings());

        public Task SaveAsync(TheForgeSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task SavePatchAsync(
            Func<TheForgeSettings, TheForgeSettings> patch,
            CancellationToken cancellationToken = default)
        {

            await Task.Yield();

            throw new IOException("the-forge.json is read-only");

        }

    }

    private sealed class StaticOptionsMonitor(TheForgeSettings current) : IOptionsMonitor<TheForgeSettings>
    {

        public TheForgeSettings CurrentValue { get; } = current;

        public TheForgeSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TheForgeSettings, string?> listener) => null;

    }

}
