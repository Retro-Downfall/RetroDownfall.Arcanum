using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class SessionCompactionTargetTests
{
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Persisted_assistant_model_owns_both_window_and_tokenizer_instead_of_default_or_later_hints(bool savedLarge)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        Estimator estimator = new();
        await using ArcanumWebApplicationFactory factory = Factory(estimator, defaultLarge: !savedLarge);
        using HttpClient client = factory.CreateAuthenticatedClient();
        string savedModel = savedLarge ? "namespace/large" : "namespace/small";
        Guid session = await SeedAsync(factory, savedModel);
        HttpResponseMessage response = await client.PostAsync($"/api/sessions/{session:D}/compact", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ApiResponse<CompactResult>? envelope = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseCompactResult);
        CompactResult result = Assert.IsType<CompactResult>(envelope?.Data);
        Assert.Equal(savedLarge, result.EntriesRemoved == 0);
        Assert.NotEmpty(estimator.Targets);
        Assert.All(estimator.Targets, target =>
        {
            Assert.Equal(savedModel, target.Model);
            Assert.Equal(savedLarge ? 128_000 : 8192, target.Window);
        });
    }

    [SkippableTheory]
    [InlineData("unresolved")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("window-conflict")]
    [InlineData("tokenizer-conflict")]
    [InlineData("blank-newest")]
    public async Task Ambiguous_or_unresolved_saved_assistant_refuses_before_any_compaction(string reason)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        Estimator estimator = new();
        await using ArcanumWebApplicationFactory factory = Factory(estimator,
            duplicate: reason is "window-conflict" or "tokenizer-conflict",
            conflictingWindow: reason == "window-conflict",
            conflictingTokenizer: reason == "tokenizer-conflict");
        using HttpClient client = factory.CreateAuthenticatedClient();
        const string privateModel = "private/model-canary-do-not-echo";
        Guid session = await SeedAsync(factory, reason == "unresolved" ? privateModel : "namespace/large", reason);
        int before = await CountAsync(factory, session);
        HttpResponseMessage response = await client.PostAsync($"/api/sessions/{session:D}/compact", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        ApiResponse<CompactResult>? envelope = JsonSerializer.Deserialize(body, ArcanumJsonContext.Default.ApiResponseCompactResult);
        Assert.Equal(ErrorCodes.Session.CompactionUnavailable, envelope?.Error?.Code);
        Assert.Contains("No entries were removed", envelope?.Error?.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(privateModel, body, StringComparison.Ordinal);
        Assert.Equal(before, await CountAsync(factory, session));
        Assert.Empty(estimator.Targets);
    }

    [SkippableFact]
    public async Task No_recorded_assistant_model_uses_the_documented_default()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        Estimator estimator = new();
        await using ArcanumWebApplicationFactory factory = Factory(estimator, defaultLarge: true);
        using HttpClient client = factory.CreateAuthenticatedClient();
        Guid session = await SeedAsync(factory, string.Empty);
        int before = await CountAsync(factory, session);
        HttpResponseMessage response = await client.PostAsync($"/api/sessions/{session:D}/compact", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await CountAsync(factory, session));
        Assert.All(estimator.Targets, target => Assert.Equal("namespace/large", target.Model));
    }

    [SkippableFact]
    public async Task Duplicate_providers_with_identical_applicable_policy_are_not_ambiguous()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        Estimator estimator = new();
        await using ArcanumWebApplicationFactory factory = Factory(estimator, duplicate: true);
        using HttpClient client = factory.CreateAuthenticatedClient();
        Guid session = await SeedAsync(factory, "namespace/large");
        int before = await CountAsync(factory, session);
        HttpResponseMessage response = await client.PostAsync($"/api/sessions/{session:D}/compact", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await CountAsync(factory, session));
        Assert.All(estimator.Targets, target => Assert.Equal("namespace/large", target.Model));
    }

    [SkippableFact]
    public async Task One_configuration_snapshot_owns_window_and_tokenizer_across_a_held_session_read()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        SessionPinAtomicityTests.PinObserver observer = new();
        await using ArcanumWebApplicationFactory factory = SessionPinAtomicityTests.Factory(observer);
        using HttpClient client = factory.CreateAuthenticatedClient();
        Guid session = await SeedAsync(factory, "namespace/large");
        using IServiceScope scope = factory.Services.CreateScope();
        IGrimoireRepository repository = scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();
        ChangingSnapshot snapshot = new(new ArcanumSettings
        {
            DefaultModel = "namespace/small",
            Providers = [Provider("large", "namespace/large", 128_000)],
        });
        Estimator estimator = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        observer.AfterSessionRead = async (id, token) =>
        {
            if (id != session) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        };
        ContextCompressionService service = new(repository, snapshot,
            new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance),
            NullLogger<ContextCompressionService>.Instance, estimator);
        Task<Result<CompactResult>> compact = service.CompactSessionAsync(session, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            snapshot.Current = new ArcanumSettings
            {
                DefaultModel = "namespace/small",
                Providers = [Provider("large", "namespace/large", 4096)],
            };
        }
        finally
        {
            release.TrySetResult();
        }
        Result<CompactResult> result = await compact.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(0, result.Value.EntriesRemoved);
        Assert.Equal(1, snapshot.Reads);
        Assert.Equal(4096, snapshot.Current.Providers[0].ContextWindowLimit);
        Assert.All(estimator.Targets, target => Assert.Equal(("namespace/large", 128_000), target));
    }

    private sealed class ChangingSnapshot(ArcanumSettings current) : IOptionsSnapshot<ArcanumSettings>
    {
        public ArcanumSettings Current { get; set; } = current;
        public int Reads { get; private set; }
        public ArcanumSettings Value
        {
            get
            {
                Reads++;
                return Current;
            }
        }

        public ArcanumSettings Get(string? name) => Value;
    }

    [SkippableTheory]
    [InlineData(0, 256)]
    [InlineData(256, 0)]
    [InlineData(-1, 256)]
    public async Task Equivalent_clamped_provider_windows_use_the_same_actual_compaction_limit(int firstWindow, int otherWindow)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        Estimator estimator = new();
        await using ArcanumWebApplicationFactory factory = Factory(estimator, duplicate: true,
            largeWindow: firstWindow, duplicateWindow: otherWindow);
        using HttpClient client = factory.CreateAuthenticatedClient();
        Guid session = await SeedAsync(factory, "namespace/large");
        int before = await CountAsync(factory, session);
        HttpResponseMessage response = await client.PostAsync($"/api/sessions/{session:D}/compact", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ApiResponse<CompactResult>? envelope = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseCompactResult);
        CompactResult result = Assert.IsType<CompactResult>(envelope?.Data);
        // Every stored message costs 1000 in this deterministic estimator, so no stored row
        // fits the normalized 256-token ceiling. Count actual durable deletions rather than
        // the estimator's deliberately retained empty-user framing cost.
        Assert.Equal(before, result.EntriesRemoved);
        Assert.Equal(0, await CountAsync(factory, session));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_saved_model_needs_an_explicit_Familiar_declaration_for_destructive_compaction(bool declared)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        // The actual host remains HTTP-only. This isolated service snapshot is never given to a
        // chat-client factory or a provider-discovery service; its only consumer is the local estimator.
        await using ArcanumWebApplicationFactory factory = Factory(new Estimator());
        using HttpClient client = factory.CreateAuthenticatedClient();
        Guid session = await SeedAsync(factory, "namespace/large");
        int before = await CountAsync(factory, session);
        using IServiceScope scope = factory.Services.CreateScope();
        Estimator estimator = new();
        ChangingSnapshot snapshot = new(new ArcanumSettings
        {
            DefaultModel = "different-default",
            Providers = [new ProviderSettings
            {
                Name = "vendor", Type = AiProviderKind.ClaudeCodeCli, Command = "arcanum-test-never-invoke",
                Models = declared ? [new ModelEntry("namespace/large")] : [], ContextWindowLimit = 128_000,
            }],
        });
        ContextCompressionService service = new(scope.ServiceProvider.GetRequiredService<IGrimoireRepository>(), snapshot,
            new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance),
            NullLogger<ContextCompressionService>.Instance, estimator);
        Result<CompactResult> result = await service.CompactSessionAsync(session, CancellationToken.None);
        Assert.Equal(declared, result.IsSuccess);
        Assert.Equal(before, await CountAsync(factory, session));
        if (declared)
        {
            Assert.NotEmpty(estimator.Targets);
            Assert.All(estimator.Targets, target => Assert.Equal(("namespace/large", 128_000), target));
        }
        else
        {
            Assert.Equal(ErrorCodes.Session.CompactionUnavailable, result.Error.Code);
            Assert.Contains("No entries were removed", result.Error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("namespace/large", result.Error.Message, StringComparison.Ordinal);
            Assert.Empty(estimator.Targets);
        }
    }

    private static ArcanumWebApplicationFactory Factory(Estimator estimator, bool defaultLarge = false,
        bool duplicate = false, bool conflictingWindow = false, bool conflictingTokenizer = false,
        int? largeWindow = null, int? duplicateWindow = null) => new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { MemoryManagement = true },
                DefaultModel = defaultLarge ? "namespace/large" : "namespace/small",
                Providers = [
                    Provider("small", "namespace/small", 8192),
                Provider("large", "namespace/large", largeWindow ?? 128_000),
                .. duplicate ? new[] { Provider(conflictingTokenizer ? "other-tokenizer" : "same-policy", "namespace/large", duplicateWindow ?? (conflictingWindow ? 4096 : 128_000)) } : [],
            ],
            },
            ServiceOverrides = services => services.Replace(ServiceDescriptor.Singleton<IModelTokenEstimator>(estimator)),
        };

    private static ProviderSettings Provider(string name, string model, int window) => new()
    {
        Name = name,
        Type = AiProviderKind.OpenAICompatible,
        Endpoint = "https://example.test/v1",
        Models = [model],
        ContextWindowLimit = window,
    };

    private static async Task<Guid> SeedAsync(ArcanumWebApplicationFactory factory, string model, string? reason = null)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();
        Guid id = Guid.NewGuid();
        DateTimeOffset sameTime = DateTimeOffset.UtcNow;
        db.Sessions.Add(new Session { Id = id, Title = "compaction model", CreatedAt = sameTime, UpdatedAt = sameTime });
        List<Entry> entries = Enumerable.Range(1, 10).Select(index => new Entry
        {
            Id = Guid.NewGuid(),
            SessionId = id,
            Sequence = index,
            CreatedAt = sameTime,
            Role = index % 2 == 0 ? MessageRole.Assistant : MessageRole.User,
            Content = "message " + index,
            ModelUsed = index % 2 == 0 ? model : "namespace/small",
        }).ToList();
        // Later request/tool hints cannot replace the most recent persisted assistant answer model.
        entries.Add(new Entry
        {
            Id = Guid.NewGuid(),
            SessionId = id,
            Sequence = 11,
            CreatedAt = sameTime,
            Role = MessageRole.User,
            Content = "new request hint",
            ModelUsed = "namespace/small"
        });
        entries.Add(new Entry
        {
            Id = Guid.NewGuid(),
            SessionId = id,
            Sequence = 12,
            CreatedAt = sameTime,
            Role = MessageRole.Assistant,
            Content = "[ToolCall: read_file({})]",
            ToolName = "read_file",
            ModelUsed = "namespace/small"
        });
        if (reason is "empty" or "whitespace" or "blank-newest")
        {
            entries.Add(new Entry
            {
                Id = Guid.NewGuid(),
                SessionId = id,
                Sequence = 13,
                CreatedAt = sameTime,
                Role = MessageRole.Assistant,
                Content = reason == "empty" ? string.Empty : reason == "whitespace" ? "\r\n\t\u2003" : "unknown attribution",
                ModelUsed = reason == "blank-newest" ? string.Empty : model
            });
        }
        db.Entries.AddRange(entries);
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<int> CountAsync(ArcanumWebApplicationFactory factory, Guid session)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ArcanumDbContext>().Entries.CountAsync(entry => entry.SessionId == session);
    }

    private sealed class Estimator : IModelTokenEstimator
    {
        public ConcurrentQueue<(string Model, int Window)> Targets { get; } = new();
        public ResolvedModelTokenizationProfile ResolveProfile(ProviderSettings provider, string model) => new()
        {
            ProfileId = "profile:" + provider.Name,
            Type = ModelTokenizationProfileType.UnknownFallback,
            TokenizerId = provider.Name == "other-tokenizer" ? "other" : "shared",
            SafetyMarginPercent = 0,
            PerMessageOverheadTokens = 0,
            PerToolOverheadTokens = 0,
            ProviderFramingTokens = 0,
            StopTokenOverheadTokens = 0,
            UnknownImageReserveTokens = 0,
            Confidence = 1,
        };
        public ResolvedModelTokenizationProfile ResolveEffectiveProfile(ProviderSettings provider, string model)
        {
            return ResolveProfile(provider, model);
        }

        public TokenEstimate EstimateText(ProviderSettings provider, string model, string? text) => throw new NotSupportedException();
        public ContextTokenBreakdown EstimateContext(ModelTokenizationRequest request)
        {
            Targets.Enqueue((request.Model, request.Provider.ContextWindowLimit));
            int tokens = request.Messages.Count * 1000;
            return new ContextTokenBreakdown
            {
                Provider = request.Provider.Name,
                Model = request.Model,
                Profile = ResolveProfile(request.Provider, request.Model),
                Components = [],
                InputTokens = tokens,
                ReservedTokens = 0,
                TotalTokens = tokens,
                OverallClassification = TokenEstimateClassification.Estimated,
                SafetyMarginTokens = 0,
            };
        }
    }
}
