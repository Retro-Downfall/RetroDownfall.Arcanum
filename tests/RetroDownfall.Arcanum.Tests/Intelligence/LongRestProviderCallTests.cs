using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

[Collection("Grimoire")]
public sealed class LongRestProviderCallTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_admission_arm_fails_before_callback_client_resolution_or_accounting(bool embedding)
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync(embedding ? LongRestProviderPayloadKind.Embedding : LongRestProviderPayloadKind.Summary);

        LongRestProviderSelection selected = f.Epoch.Capture().ResolveChat().Value;

        LongRestProviderAdmission admission = embedding
            ? new LongRestProviderAdmission.Chat(new(selected.Provider, selected.Model, 2048, 0))
            : new LongRestProviderAdmission.Embedding(selected.Provider, selected.Model, 64, 1, new(new byte[32]));
        int fences = 0;

        Result<LongRestProviderResult> result = await f.Provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance,
            _ => { fences++; return Task.CompletedTask; }, admission, CancellationToken.None);
        Assert.True(result.IsFailure);

        Assert.Equal(0, fences);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM InferenceRuns;"));
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("tokens")]
    [InlineData("output")]
    [InlineData("reasoning")]
    [InlineData("context")]
    public async Task Changed_chat_admission_is_refused_before_send(string change)
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync();

        LongRestProviderAdmission.Chat admitted = (LongRestProviderAdmission.Chat)f.Admission();

        var context = admitted.Context;

        context = change switch
        {
            "fingerprint" => context with { PrecomputedBreakdown = context.PrecomputedBreakdown! with { PayloadFingerprint = "changed" } },
            "tokens" => context with { PrecomputedBreakdown = context.PrecomputedBreakdown! with { InputTokens = 1 } },
            "output" => context with { ReservedAnswerTokens = 1 },
            "reasoning" => context with { ReservedReasoningTokens = 1 },
            _ => context with { Provider = context.Provider with { ContextWindowLimit = 1 } },
        };

        int fences = 0;

        Result<LongRestProviderResult> result = await f.Provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance,
            _ => { fences++; return Task.CompletedTask; }, new LongRestProviderAdmission.Chat(context), CancellationToken.None);
        Assert.True(result.IsFailure);

        Assert.Equal(0, fences);

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("tokens")]
    [InlineData("dimensions")]
    [InlineData("model")]
    public async Task Embedding_admission_binds_the_exact_sole_input_model_dimensions_and_tokens(string change)
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync(LongRestProviderPayloadKind.Embedding);

        LongRestProviderAdmission.Embedding admitted = (LongRestProviderAdmission.Embedding)f.Admission();

        admitted = change switch
        {
            "fingerprint" => admitted with { InputFingerprint = new(new byte[32]) },
            "tokens" => admitted with { ReservedInputTokens = admitted.ReservedInputTokens + 1 },
            "dimensions" => admitted with { Dimensions = 65 },
            _ => admitted with { Model = "other" },
        };

        Result<LongRestProviderResult> result = await f.Provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance,
            _ => throw new InvalidOperationException("Fence must not run"), admitted, CancellationToken.None);
        Assert.True(result.IsFailure);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Each_executor_arm_itself_refuses_the_opposite_closed_payload(bool embeddingExecutor)
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync(embeddingExecutor ? LongRestProviderPayloadKind.Summary : LongRestProviderPayloadKind.Embedding);

        LongRestProviderClientFactory factory = f.ClientFactory;

        ModelCallExecutor executor = new(f.Estimator);

        int fences = 0;

        int paid = 0;

        Task Before(CancellationToken _) { fences++; return Task.CompletedTask; }
        Task Paid(LongRestProviderUsage _) { paid++; return Task.CompletedTask; }
        Result<LongRestProviderResult> result;

        if (embeddingExecutor)
        {
            using EmbeddingGeneratorLease lease = (await factory.ResolveEmbeddingAsync(f.Epoch.Capture().ResolveEmbedding().Value, CancellationToken.None)).Value;
            result = await executor.ExecuteLongRestEmbeddingAsync(lease.Generator, f.Payload, UnrestrictedTurnBudget.Instance, Before, Paid, f.Admission(), CancellationToken.None);
        }
        else
        {
            using ChatClientLease lease = (await factory.ResolveChatAsync(f.Epoch.Capture().ResolveChat().Value, CancellationToken.None)).Value;
            result = await executor.ExecuteLongRestChatAsync(lease.ChatClient, f.Payload, UnrestrictedTurnBudget.Instance, Before, Paid, f.Admission(), CancellationToken.None);
        }

        Assert.True(result.IsFailure);

        Assert.Equal(0, fences);

        Assert.Equal(0, paid);

        Assert.Equal(0, f.Handler.SendCount);
    }

    // The renderer still refuses to truncate. Planner and renderer now measure one layout, so a memory whose raw content is under the
    // 32,768-byte ceiling but whose request is not is refused by the planner, before any payload, effect row or send exists.
    [Fact]
    public async Task Rendered_chat_overflow_is_refused_by_the_planner_instead_of_truncating_sources()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await h.SeedAsync(Guid.NewGuid().ToString("D"), new string('x', 32749));

        await h.SeedAsync(Guid.NewGuid().ToString("D"), "Beta observation.");

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        Result<LongRestHierarchyWork?> planned = await h.Store.ReadNextFoldAsync(generation.GenerationId, null, CancellationToken.None);

        Assert.True(planned.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.ModelContextTooSmall, planned.Error.Code);

        Assert.Equal(0L, await h.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects;"));

        Assert.Equal(0L, await h.ScalarAsync("SELECT count(*) FROM InferenceRuns;"));
    }

    [Fact]
    public async Task Unsupported_transport_cannot_resolve_or_start_accounting()
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync();

        ArcanumSettings settings = LongRestConfigurationEpochTests.Settings();

        settings.Providers[0].Type = AiProviderKind.CodexCli;

        f.Options.Set(settings);

        // The frozen work is also stale; unsupported direct factory resolution proves no SDK construction.
        LongRestProviderClientFactory factory = f.ClientFactory;

        Assert.True((await factory.ResolveChatAsync(f.Epoch.Capture().ResolveChat().Value, CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM InferenceRuns;"));
    }

    [Fact]
    public async Task Budget_refusal_prevents_disclosure_and_send()
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync();

        LongRestProvider provider = new(f.ClientFactory, new ModelCallExecutor(f.Estimator), f.Epoch,
            new RetroDownfall.Arcanum.Infrastructure.Data.TurnRunWriter(f.H.Saga.Context), new RefusedBudget());
        int callbacks = 0;

        Result<LongRestProviderResult> result = await provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance,
            _ => { callbacks++; return Task.CompletedTask; }, f.Admission(), CancellationToken.None);
        Assert.True(result.IsFailure);

        Assert.Equal(0, callbacks);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"));

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT Status FROM InferenceRuns;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolver_fault_returns_a_safe_failure_without_exposing_its_exception_body(bool embedding)
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync();
        LongRestProviderFixture.CountingProvider handler = new();

        using LongRestProviderClientFactory factory = new(new FailedKeys(), handler);
        bool failed = false;
        string? message = null;
        Exception? exception = await Record.ExceptionAsync(async () =>
        {
            if (embedding)
            {
                Result<EmbeddingGeneratorLease> result = await factory.ResolveEmbeddingAsync(f.Epoch.Capture().ResolveEmbedding().Value, CancellationToken.None);
                failed = result.IsFailure;
                message = result.Error.Message;
            }
            else
            {
                Result<ChatClientLease> result = await factory.ResolveChatAsync(f.Epoch.Capture().ResolveChat().Value, CancellationToken.None);
                failed = result.IsFailure;
                message = result.Error.Message;
            }
        });

        Assert.Null(exception);
        Assert.True(failed);
        Assert.DoesNotContain("protected-resolver-body", message!, StringComparison.Ordinal);
        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0, handler.SendCount);
    }

    private sealed class FailedKeys : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken) =>
            Task.FromException<string?>(new InvalidOperationException("protected-resolver-body"));
    }

    private sealed class RefusedBudget : IBudgetReservationService
    {
        public Task<Result<BudgetReservation>> ReserveAsync(BudgetReservationRequest request, CancellationToken cancellationToken = default) => Task.FromResult(Result<BudgetReservation>.Failure(new("Budget.Exceeded", "Refused")));

        public Task<Result> AdjustAsync(Guid reservationId, decimal reservedUsd, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result> RecheckDailyLimitAsync(Guid reservationId, decimal delegatedSpendUsd, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ExtendExpiryAsync(Guid reservationId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ReconcileAsync(Guid reservationId, decimal actualCostUsd, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<decimal> GetTodayCommittedSpendAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<decimal> GetTodayOutstandingReservationsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> SweepExpiredAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
