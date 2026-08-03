using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.Tokenizers;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ManaPreflightTests
{

    private static ManaPreflight CreatePreflight() =>
        new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

    [Theory]
    [InlineData(0, 3, true)]
    [InlineData(3, 3, true)]
    [InlineData(4, 3, false)]
    public void ShouldSkipCompressionPreflight_RespectsMinMessages(int count, int min, bool expected)
    {
        List<MeAiChatMessage> messages = Enumerable.Range(0, count)
            .Select(static i => new MeAiChatMessage(ChatRole.User, $"m{i}"))
            .ToList();

        bool skip = CreatePreflight().ShouldSkipCompressionPreflight(messages, min);

        Assert.Equal(expected, skip);
    }

    [Fact]
    public void CountTokens_IncludesOverheadAndMessageText()
    {
        Tokenizer tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

        List<MeAiChatMessage> messages =
        [
            new MeAiChatMessage(ChatRole.User, "hello world"),
            new MeAiChatMessage(ChatRole.Assistant, [new TextContent("assistant reply")]),
        ];

        int count = CreatePreflight().CountTokens(messages, tokenizer, perMessageOverheadTokens: 4, "o200k_base");

        int textOnly = tokenizer.CountTokens("hello world") + tokenizer.CountTokens("assistant reply");

        Assert.Equal(textOnly + 8, count);
    }

    [Fact]
    public void CountTokens_EmptyMessages_ReturnsOverheadOnly()
    {
        Tokenizer tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

        List<MeAiChatMessage> messages = [new MeAiChatMessage(ChatRole.User, string.Empty)];

        int count = CreatePreflight().CountTokens(messages, tokenizer, perMessageOverheadTokens: 5, "o200k_base");

        Assert.Equal(5, count);
    }

}

public sealed class InferenceTokenizerResolverTests
{

    [Fact]
    public void ResolveTokenizer_DefaultEncoding_ReturnsTokenizer()
    {
        InferenceTokenizerResolver resolver = new(NullLogger<InferenceTokenizerResolver>.Instance);

        Tokenizer tokenizer = resolver.ResolveTokenizer(null);

        Assert.NotNull(tokenizer);

        Assert.True(tokenizer.CountTokens("abc") > 0);
    }

    [Fact]
    public void ResolveTokenizer_CachesByEncodingName()
    {
        InferenceTokenizerResolver resolver = new(NullLogger<InferenceTokenizerResolver>.Instance);

        Tokenizer first = resolver.ResolveTokenizer("o200k_base");

        Tokenizer second = resolver.ResolveTokenizer("o200k_base");

        Assert.Same(first, second);
    }

    [Fact]
    public void ResolveTokenizer_UnknownEncoding_FallsBackWithoutThrowing()
    {
        InferenceTokenizerResolver resolver = new(NullLogger<InferenceTokenizerResolver>.Instance);

        Tokenizer tokenizer = resolver.ResolveTokenizer("not-a-real-encoding-name");

        Assert.NotNull(tokenizer);

        Assert.True(tokenizer.CountTokens("fallback") > 0);
    }

}

public sealed class ArcanumBuiltInToolsTests
{

    [Fact]
    public async Task LocalTimeTool_ReturnsIsoTimestamp()
    {
        ArcanumLocalTimeTool tool = new();

        object? result = await tool.InvokeAsync(new AIFunctionArguments());

        Assert.NotNull(result);

        Assert.True(DateTime.TryParse(result!.ToString(), out _));
    }

    [Fact]
    public async Task SystemInfoTool_ReturnsRuntimeDetails()
    {
        ArcanumSystemInfoTool tool = new();

        object? result = await tool.InvokeAsync(new AIFunctionArguments());

        string text = Assert.IsType<string>(result);

        Assert.Contains("OS:", text, StringComparison.Ordinal);

        Assert.Contains(".NET Runtime:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltInTools_ExposeExpectedNamesAndSchemas()
    {
        ArcanumLocalTimeTool local = new();

        ArcanumSystemInfoTool system = new();

        Assert.Equal("get_local_system_time", local.Name);

        Assert.Equal("get_arcanum_system_info", system.Name);

        Assert.Equal(JsonValueKind.Object, local.JsonSchema.ValueKind);

        Assert.Equal(JsonValueKind.Object, system.JsonSchema.ValueKind);
    }

    [Fact]
    public void BuiltInToolRegistry_AdvertisesCanonicalWebToolsButNotLegacyAlias()
    {
        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { WebBrowsing = true },
        };
        BuiltInToolRegistry registry = new(
            new StubHttpClientFactory(),
            new TestOptionsSnapshot<ArcanumSettings>(settings));

        IReadOnlyList<string> names = registry.GetToolNames();

        Assert.Contains(ArcanumBuiltInToolNames.WebSearch, names);
        Assert.Contains(ArcanumBuiltInToolNames.ReadUrl, names);
        Assert.DoesNotContain(ArcanumBuiltInToolNames.BrowseWeb, names);
    }

    [Fact]
    public async Task BuiltInToolRegistry_UnexpectedFailure_UsesModelSafeToolContract()
    {
        const string canary = "CANARY_TOOL_ARGUMENT_FILE_CONTENT";
        TestCapturingLogger<BuiltInToolRegistry> logger = new();
        BuiltInToolRegistry registry = new(
            new StubHttpClientFactory(),
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            browseWebLogger: null,
            logger: logger);
        JsonElement disposedArguments;

        using (JsonDocument document = JsonDocument.Parse(
            $$"""{"secret":"{{canary}}"}"""))
        {
            disposedArguments = document.RootElement;
        }

        Result<JsonElement> result = await registry.InvokeAsync(
            ArcanumLocalTimeTool.ToolName,
            disposedArguments,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Hub.Error, result.Error.Code);
        Assert.Equal(
            ToolExecutionPipeline.PublicToolFailureMessage(ArcanumLocalTimeTool.ToolName),
            result.Error.Message);
        Assert.DoesNotContain(canary, result.Error.Message, StringComparison.Ordinal);

        TestLogEntry log = Assert.Single(logger.Entries);
        Assert.Null(log.Exception);
        Assert.DoesNotContain(canary, log.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ObjectDisposedException), log.Message, StringComparison.Ordinal);
        Assert.Contains(ArcanumLocalTimeTool.ToolName, log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuiltInToolRegistry_Cancellation_Propagates()
    {
        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { WebBrowsing = true },
        };
        BuiltInToolRegistry registry = new(
            new StubHttpClientFactory(new CancellingHandler()),
            new TestOptionsSnapshot<ArcanumSettings>(settings));
        using JsonDocument document = JsonDocument.Parse(
            """{"url":"https://example.test/"}""");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            registry.InvokeAsync(
                ArcanumBrowseWebTool.ToolName,
                document.RootElement,
                cancellation.Token));
    }

    [Fact]
    public async Task BuiltInToolRegistry_BrowseFailure_SanitizesModelOutputAndLogs()
    {
        const string canary = "CANARY_BROWSE_PROVIDER_RESPONSE_AND_URL";
        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { WebBrowsing = true },
        };
        TestCapturingLogger<ArcanumBrowseWebTool> logger = new();
        BuiltInToolRegistry registry = new(
            new StubHttpClientFactory(new ThrowingHandler(canary)),
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            logger);
        using JsonDocument document = JsonDocument.Parse(
            """{"url":"https://example.com/private-path"}""");

        Result<JsonElement> result = await registry.InvokeAsync(
            ArcanumBrowseWebTool.ToolName,
            document.RootElement,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        BrowseWebResult? output = result.Value.Deserialize(
            ArcanumJsonContext.Default.BrowseWebResult);
        Assert.NotNull(output);
        Assert.Equal(
            ToolExecutionPipeline.PublicToolFailureMessage(ArcanumBrowseWebTool.ToolName),
            output.Content);

        string serialized = result.Value.GetRawText();
        Assert.DoesNotContain(canary, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-path", serialized, StringComparison.Ordinal);

        TestLogEntry log = Assert.Single(logger.Entries);
        Assert.Null(log.Exception);
        Assert.DoesNotContain(canary, log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-path", log.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), log.Message, StringComparison.Ordinal);
        Assert.Contains(ArcanumBrowseWebTool.ToolName, log.Message, StringComparison.Ordinal);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler? handler = null)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            handler is null
                ? new HttpClient()
                : new HttpClient(handler, disposeHandler: false);
    }

    private sealed class CancellingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(
                new OperationCanceledException("tool provider cancelled"));
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(
                new InvalidOperationException(message));
    }

}

public sealed class HumanPromptRegistryTests
{

    [Fact]
    public async Task WaitAndSubmit_ReturnsResponse()
    {
        HumanPromptRegistry registry = new();

        string promptId = Guid.NewGuid().ToString();

        Task<string> waitTask = registry.WaitForResponseAsync(promptId, CancellationToken.None);

        Assert.True(registry.TrySubmitResponse(promptId, "approved"));

        string response = await waitTask;

        Assert.Equal("approved", response);

        Assert.Equal(0, registry.WaiterCountForTesting);

        Assert.Equal(HumanPromptRegistry.MaxConcurrentWaiters, registry.AvailableSlotsForTesting);
    }

    [Fact]
    public void TrySubmitResponse_UnknownPrompt_ReturnsFalse()
    {
        HumanPromptRegistry registry = new();

        bool submitted = registry.TrySubmitResponse(Guid.NewGuid().ToString(), "nope");

        Assert.False(submitted);
    }

    [Fact]
    public async Task WaitForResponse_DuplicatePromptId_Throws()
    {
        HumanPromptRegistry registry = new();

        string promptId = Guid.NewGuid().ToString();

        _ = registry.WaitForResponseAsync(promptId, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.WaitForResponseAsync(promptId, CancellationToken.None));
    }

    [Fact]
    public async Task WaitForResponse_Cancellation_CancelsTaskAndEvictsWaiter()
    {
        HumanPromptRegistry registry = new();

        using CancellationTokenSource cts = new();

        Task<string> waitTask = registry.WaitForResponseAsync(Guid.NewGuid().ToString(), cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);

        Assert.Equal(0, registry.WaiterCountForTesting);

        Assert.Equal(HumanPromptRegistry.MaxConcurrentWaiters, registry.AvailableSlotsForTesting);
    }

    [Fact]
    public async Task WaitForResponse_HasNoRegistryDeadline()
    {
        HumanPromptRegistry registry = new();

        string promptId = Guid.NewGuid().ToString("N");

        Task<string> waitTask = registry.WaitForResponseAsync(promptId, CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.False(waitTask.IsCompleted);

        Assert.True(registry.TrySubmitResponse(promptId, "continued"));

        Assert.Equal("continued", await waitTask);

        Assert.Equal(0, registry.WaiterCountForTesting);
    }

    [Fact]
    public async Task WaitForResponse_CapacityQueuesUntilAnActiveWaiterCompletes()
    {
        HumanPromptRegistry registry = new();

        List<(string Id, Task<string> Wait)> tracked = [];

        for (int i = 0; i < HumanPromptRegistry.MaxConcurrentWaiters; i++)
        {
            string id = Guid.NewGuid().ToString("N");

            tracked.Add((id, registry.WaitForResponseAsync(id, CancellationToken.None)));
        }

        string queuedId = Guid.NewGuid().ToString("N");

        Task<string> queued = registry.WaitForResponseAsync(
            queuedId,
            CancellationToken.None);

        await Task.Yield();

        Assert.False(queued.IsCompleted);

        Assert.Equal(HumanPromptRegistry.MaxConcurrentWaiters, registry.WaiterCountForTesting);

        Assert.Equal(0, registry.AvailableSlotsForTesting);

        Assert.True(registry.TrySubmitResponse(tracked[0].Id, "first"));

        Assert.Equal("first", await tracked[0].Wait);

        while (!registry.TrySubmitResponse(queuedId, "queued"))
        {
            await Task.Yield();
        }

        Assert.Equal("queued", await queued);

        foreach ((string id, Task<string> wait) in tracked.Skip(1))
        {
            Assert.True(registry.TrySubmitResponse(id, "ok"));

            Assert.Equal("ok", await wait);
        }

        Assert.Equal(0, registry.WaiterCountForTesting);

        Assert.Equal(HumanPromptRegistry.MaxConcurrentWaiters, registry.AvailableSlotsForTesting);
    }

    [Fact]
    public async Task CreateReservation_SubmitDoesNotReleaseCapacity_UntilDispose()
    {
        HumanPromptRegistry registry = new();

        IHumanPromptReservation reservation = await registry.CreateReservationAsync(
            CancellationToken.None);

        Assert.NotNull(reservation);

        Assert.Equal(1, registry.WaiterCountForTesting);

        Assert.Equal(HumanPromptRegistry.MaxConcurrentWaiters - 1, registry.AvailableSlotsForTesting);

        Task<string> waitTask = reservation.WaitAsync(CancellationToken.None);

        Assert.True(registry.TrySubmitResponse(reservation.PromptId, "held"));

        Assert.Equal("held", await waitTask);

        // Submit completed the waiter but capacity remains owned until dispose.
        Assert.Equal(1, registry.WaiterCountForTesting);

        Assert.Equal(HumanPromptRegistry.MaxConcurrentWaiters - 1, registry.AvailableSlotsForTesting);

        await reservation.DisposeAsync();

        Assert.Equal(0, registry.WaiterCountForTesting);

        Assert.Equal(HumanPromptRegistry.MaxConcurrentWaiters, registry.AvailableSlotsForTesting);
    }

    [Fact]
    public async Task AwaitReservedAsync_WaitsWithoutReleasingCapacity()
    {
        HumanPromptRegistry registry = new();

        IHumanPromptReservation reservation = await registry.CreateReservationAsync(
            CancellationToken.None);

        Assert.NotNull(reservation);

        Task<string> awaitTask = registry.AwaitReservedAsync(
            reservation.PromptId,
            CancellationToken.None);

        Assert.True(registry.TrySubmitResponse(reservation.PromptId, "from-tool"));

        Assert.Equal("from-tool", await awaitTask);

        Assert.Equal(1, registry.WaiterCountForTesting);

        Assert.Equal(HumanPromptRegistry.MaxConcurrentWaiters - 1, registry.AvailableSlotsForTesting);

        await reservation.DisposeAsync();

        Assert.Equal(0, registry.WaiterCountForTesting);
    }

    [Fact]
    public async Task Reservation_CallerCancellation_DoesNotReleaseCapacity_UntilDispose()
    {
        HumanPromptRegistry registry = new();

        IHumanPromptReservation reservation = await registry.CreateReservationAsync(
            CancellationToken.None);

        Assert.NotNull(reservation);

        using CancellationTokenSource cts = new();

        Task<string> waitTask = reservation.WaitAsync(cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);

        Assert.Equal(1, registry.WaiterCountForTesting);

        Assert.Equal(HumanPromptRegistry.MaxConcurrentWaiters - 1, registry.AvailableSlotsForTesting);

        Assert.False(registry.TrySubmitResponse(reservation.PromptId, "too-late"));

        await reservation.DisposeAsync();

        Assert.Equal(0, registry.WaiterCountForTesting);

        Assert.Equal(HumanPromptRegistry.MaxConcurrentWaiters, registry.AvailableSlotsForTesting);
    }

    [Fact]
    public async Task CreateReservation_CapacityQueuesUntilDispose()
    {
        HumanPromptRegistry registry = new();

        List<IHumanPromptReservation> held = [];

        for (int i = 0; i < HumanPromptRegistry.MaxConcurrentWaiters; i++)
        {
            IHumanPromptReservation reservation = await registry.CreateReservationAsync(
                CancellationToken.None);

            Assert.NotNull(reservation);

            held.Add(reservation);
        }

        Task<IHumanPromptReservation> queued = registry.CreateReservationAsync(
            CancellationToken.None);

        await Task.Yield();

        Assert.False(queued.IsCompleted);

        Assert.Equal(0, registry.AvailableSlotsForTesting);

        await held[0].DisposeAsync();

        IHumanPromptReservation admitted = await queued;

        Assert.Equal(0, registry.AvailableSlotsForTesting);

        await admitted.DisposeAsync();

        foreach (IHumanPromptReservation reservation in held.Skip(1))
        {
            await reservation.DisposeAsync();
        }

        Assert.Equal(HumanPromptRegistry.MaxConcurrentWaiters, registry.AvailableSlotsForTesting);
    }

}
