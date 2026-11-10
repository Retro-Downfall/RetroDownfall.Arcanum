using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.A2A;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Remote_callback_HTTP_commits_receipt_before_acknowledgement_even_with_a_live_waiter(bool liveWaiter)
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        RemoteCallbackSetup bound = await PrepareRemoteCallbackAsync(fixture);

        fixture.Settings.CurrentValue.Integrations.A2A.PushNotifications = true;

        RemoteCallbackScheduler scheduler = new();

        ServiceCollection services = new();

        services.AddSingleton(new GuildRemoteCallbackReceiver(fixture.Store, fixture.Policy, scheduler));

        await using ServiceProvider provider = services.BuildServiceProvider();

        A2ASendingCallbackRegistry callbacks = new(NullLogger<A2ASendingCallbackRegistry>.Instance);

        using SemaphoreSlim signal = new(0);

        using IDisposable? registered = liveWaiter ? callbacks.Register(bound.ConfigId, A2ACallbackToken.Hash(bound.Token), signal) : null;

        DefaultHttpContext context = new();

        context.Request.Headers[A2APushNotificationHeaders.NotificationToken] = bound.Token;

        IResult result = await A2ACallbackEndpoints.HandleAsync(bound.ConfigId, callbacks, fixture.Settings,
            provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance, context, default);

        Assert.Equal(StatusCodes.Status202Accepted, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM guild_remote_callbacks WHERE CallbackReceivedAtUtc IS NOT NULL"));

        Assert.Equal(liveWaiter ? 1 : 0, signal.CurrentCount);

        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));
    }
}
