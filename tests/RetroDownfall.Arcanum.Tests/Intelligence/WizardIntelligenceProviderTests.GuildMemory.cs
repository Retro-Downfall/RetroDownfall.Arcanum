using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Guild;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData("buffered")]
    [InlineData("streamed")]
    [InlineData("preview")]
    [InlineData("preview-no-retrieval")]
    public async Task Guild_ordinary_context_path_is_unavailable_before_provider_or_embedding_effects(string entry)
    {
        await using var f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        var execution = await f.RootAsync();

        ScriptingChatClient chat = new();

        chat.EnqueueText("must not disclose");

        FakeRagWeaveService weave = new();

        var wizard = CreateWizard(chat, weaveService: weave);

        using var invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        if (entry.StartsWith("preview", StringComparison.Ordinal))
        {
            var result = await wizard.PreviewContextAsync(new(Prompt: "query", Model: ModelName, NoRetrieval: entry == "preview-no-retrieval"), InvocationContexts.Inspection(), default);

            Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, result.Error.Code);
        }
        else if (entry == "buffered")
        {
            var result = await wizard.ExecutePromptAsync(BaseRequest() with { Prompt = "query" }, InvocationContexts.StatelessOperator(), default);

            Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, result.Error.Code);
        }
        else
        {
            var frames = new List<IntelligenceEvent>();

            await foreach (var frame in wizard.StreamPromptAsync(BaseRequest() with { Prompt = "query" }, InvocationContexts.StatelessOperator(), default)) frames.Add(frame);

            Assert.Contains(frames, frame => frame.Data == ErrorCodes.Guild.AuthorityUnavailable);
        }

        Assert.Equal(0, chat.BufferedCallCount);

        Assert.Equal(0, chat.StreamingCallCount);

        Assert.Equal(0, weave.EmbedCallCount);
    }
}
