using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildWorkspaceLeaseBrokerTests
{
    [Fact]
    public async Task Terminal_acquire_replay_survives_file_replaced_by_directory_on_every_shipping_platform()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        string path = Path.Combine(fixture.Root, "former-file.txt");

        await File.WriteAllTextAsync(path, "accepted bytes");

        var acquired = await fixture.AcquireAsync("former-file.txt");

        Assert.True(acquired.IsSuccess, acquired.Error.Message);

        Guid release = Guid.NewGuid();

        Assert.True((await fixture.Broker.ReleaseAsync(fixture.Policy.Caller, new(release, new string('B', 64)), acquired.Value.Id,
            new(release, acquired.Value.Revision, acquired.Value.Epoch), default)).IsSuccess);

        File.Delete(path);

        Directory.CreateDirectory(path);

        var replay = await fixture.Broker.AcquireAsync(fixture.Policy.Caller, fixture.LastFirstMutation!, fixture.LastFirstRequest!, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(GuildWorkspaceLeaseState.Released, replay.Value.State);

        Assert.Equal(acquired.Value.Receipt! with { Replayed = true }, replay.Value.Receipt);

        Assert.True((await fixture.AcquireAsync("former-file.txt")).IsFailure);
    }

    [SkippableFact]
    public async Task Terminal_acquire_replay_uses_durable_facts_after_a_former_path_becomes_an_escaping_link()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        string path = Path.Combine(fixture.Root, "former.txt");

        string outside = fixture.Root + "-outside.txt";

        await File.WriteAllTextAsync(path, "accepted bytes");

        var acquired = await fixture.AcquireAsync("former.txt");

        Assert.True(acquired.IsSuccess, acquired.Error.Message);

        Guid release = Guid.NewGuid();

        Assert.True((await fixture.Broker.ReleaseAsync(fixture.Policy.Caller, new(release, new string('B', 64)), acquired.Value.Id,
            new(release, acquired.Value.Revision, acquired.Value.Epoch), default)).IsSuccess);

        File.Delete(path);

        await File.WriteAllTextAsync(outside, "outside bytes");

        try
        {
            try
            {
                File.CreateSymbolicLink(path, outside);
            }
            catch (Exception exception) when (OperatingSystem.IsWindows() && exception is (UnauthorizedAccessException or IOException))
            {
                Skip.If(true, "Windows symbolic-link creation is unavailable without the required privilege.");
            }

            var replay = await fixture.Broker.AcquireAsync(fixture.Policy.Caller, fixture.LastFirstMutation!, fixture.LastFirstRequest!, default);

            Assert.True(replay.IsSuccess, replay.Error.Message);

            Assert.Equal(GuildWorkspaceLeaseState.Released, replay.Value.State);

            Assert.Equal(acquired.Value.Receipt! with { Replayed = true }, replay.Value.Receipt);

            Assert.True((await fixture.AcquireAsync("former.txt")).IsFailure);
        }
        finally
        {
            File.Delete(path);

            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Acquire_replay_returns_current_terminal_state_with_original_acceptance_receipt()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        var acquired = await fixture.AcquireAsync("a.txt");

        Assert.True(acquired.IsSuccess, acquired.Error.Message);

        Assert.NotNull(acquired.Value.Receipt);

        GuildMutationReceiptDto accepted = acquired.Value.Receipt!;

        Assert.False(accepted.Replayed);

        Assert.Equal(1, accepted.AcceptedRevision);

        fixture.Time.Advance(TimeSpan.FromMinutes(5));

        Guid renewKey = Guid.NewGuid();

        var renewed = await fixture.Broker.RenewAsync(fixture.Policy.Caller, new(renewKey, new string('B', 64)), acquired.Value.Id,
            new(renewKey, acquired.Value.Revision, acquired.Value.Epoch), default);

        Assert.True(renewed.IsSuccess, renewed.Error.Message);

        Guid releaseKey = Guid.NewGuid();

        var released = await fixture.Broker.ReleaseAsync(fixture.Policy.Caller, new(releaseKey, new string('C', 64)), renewed.Value.Id,
            new(releaseKey, renewed.Value.Revision, renewed.Value.Epoch), default);

        Assert.True(released.IsSuccess, released.Error.Message);

        var replay = await fixture.Broker.AcquireAsync(fixture.Policy.Caller, fixture.LastFirstMutation!, fixture.LastFirstRequest!, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(GuildWorkspaceLeaseState.Released, replay.Value.State);

        Assert.True(replay.Value.Revision > acquired.Value.Revision);

        Assert.Equal(accepted with { Replayed = true }, replay.Value.Receipt);
    }
}
