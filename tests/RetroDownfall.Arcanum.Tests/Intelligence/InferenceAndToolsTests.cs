using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.Tokenizers;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
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
    }

    [Fact]
    public async Task WaitForResponse_HardCeiling_ThrowsTimeoutAndEvictsWaiter()
    {
        HumanPromptRegistry registry = new()
        {
            CeilingForTesting = TimeSpan.FromMilliseconds(50),
        };

        string promptId = Guid.NewGuid().ToString("N");

        Task<string> waitTask = registry.WaitForResponseAsync(promptId, CancellationToken.None);

        HumanPromptTimeoutException ex = await Assert.ThrowsAsync<HumanPromptTimeoutException>(() => waitTask);

        Assert.Equal(HumanPromptTimeoutException.DefaultMessage, ex.Message);

        Assert.Equal(0, registry.WaiterCountForTesting);

        Assert.False(registry.TrySubmitResponse(promptId, "too-late"));
    }

    [Fact]
    public async Task WaitForResponse_CapExhaustion_ThrowsAndLeavesExistingWaiters()
    {
        HumanPromptRegistry registry = new();

        List<(string Id, Task<string> Wait)> tracked = [];

        for (int i = 0; i < HumanPromptRegistry.MaxConcurrentWaiters; i++)
        {
            string id = Guid.NewGuid().ToString("N");

            tracked.Add((id, registry.WaitForResponseAsync(id, CancellationToken.None)));
        }

        await Assert.ThrowsAsync<HumanPromptCapExceededException>(() =>
            registry.WaitForResponseAsync(Guid.NewGuid().ToString("N"), CancellationToken.None));

        Assert.Equal(HumanPromptRegistry.MaxConcurrentWaiters, registry.WaiterCountForTesting);

        foreach ((string id, Task<string> wait) in tracked)
        {
            Assert.True(registry.TrySubmitResponse(id, "ok"));

            Assert.Equal("ok", await wait);
        }

        Assert.Equal(0, registry.WaiterCountForTesting);
    }

}
