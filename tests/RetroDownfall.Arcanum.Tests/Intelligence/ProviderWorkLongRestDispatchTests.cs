using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

[Collection("Grimoire")]
public sealed class ProviderWorkLongRestDispatchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route_change_during_Long_Rest_fence_uses_exact_not_sent_control_transfer(bool embedding)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync(embedding
            ? LongRestProviderPayloadKind.Embedding : LongRestProviderPayloadKind.Summary);

        ArcanumSettings settings = f.Options.CurrentValue;

        settings.Providers[0].LocalRuntimeId = "desktop";

        settings.LocalRuntimes = [new() { Id = "desktop", Adapter = "ollama", ManagementEndpoint = "http://localhost:11434", ResourceGroup = "original-group" }];

        f.Options.Set(settings);

        using LongRestProviderClientFactory factory = new(new LongRestProviderFixture.NoKeys(), f.Handler,
            NullLogger<LongRestProviderClientFactory>.Instance, f.Options);

        ModelCallExecutor executor = new(f.Estimator, f.Options, f.Log);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int paid = 0;

        async Task Before(CancellationToken token)
        {
            entered.TrySetResult();

            await release.Task.WaitAsync(token);
        }

        Task Record(LongRestProviderUsage _) { paid++; return Task.CompletedTask; }

        async Task DispatchAsync()
        {
            if (embedding)
            {
                using EmbeddingGeneratorLease lease = (await factory.ResolveEmbeddingAsync(f.Epoch.Capture().ResolveEmbedding().Value, deadline.Token)).Value;

                await executor.ExecuteLongRestEmbeddingAsync(lease.Generator, f.Payload, UnrestrictedTurnBudget.Instance, Before, Record, f.Admission(), deadline.Token);
            }
            else
            {
                using ChatClientLease lease = (await factory.ResolveChatAsync(f.Epoch.Capture().ResolveChat().Value, deadline.Token)).Value;

                await executor.ExecuteLongRestChatAsync(lease.ChatClient, f.Payload, UnrestrictedTurnBudget.Instance, Before, Record, f.Admission(), deadline.Token);
            }
        }

        Task sending = DispatchAsync();

        await entered.Task.WaitAsync(deadline.Token);

        f.Options.Set(settings with { LocalRuntimes = [] });

        release.TrySetResult();

        Exception? failure = await Xunit.Record.ExceptionAsync(() => sending.WaitAsync(deadline.Token));

        Assert.Equal(0, f.Handler.SendCount);

        LongRestBeforeSendRefusal refusal = Assert.IsType<LongRestBeforeSendRefusal>(failure);

        Assert.Equal(ErrorCodes.LongRest.ConfigurationChanged, refusal.Error.Code);

        Assert.Equal(0, paid);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));
    }
}
