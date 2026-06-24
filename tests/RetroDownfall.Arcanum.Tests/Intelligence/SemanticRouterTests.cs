using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Workspace;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class SemanticRouterTests
{

    [Fact]
    public async Task DetermineActiveSpellAsync_EmptyCatalog_ReturnsNull()
    {
        FakeChatClient client = new();

        SpellMetadata? result = await SemanticRouter.DetermineActiveSpellAsync(
            client,
            "any prompt",
            [],
            TimeSpan.FromSeconds(5),
            maxOutputTokens: 32,
            temperature: 0f,
            CancellationToken.None);

        Assert.Null(result);

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task DetermineActiveSpellAsync_MatchingJson_ReturnsSpell()
    {
        FakeChatClient client = new()
        {
            NextText = """{"spellName":"Summoner"}""",
        };

        List<SpellMetadata> spells =
        [
            new SpellMetadata("Summoner", "summon things", "/spells/summoner/SPELL.md"),
            new SpellMetadata("Other", "other", "/spells/other/SPELL.md"),
        ];

        SpellMetadata? result = await SemanticRouter.DetermineActiveSpellAsync(
            client,
            "please summon",
            spells,
            TimeSpan.FromSeconds(5),
            maxOutputTokens: 32,
            temperature: 0f,
            CancellationToken.None);

        Assert.NotNull(result);

        Assert.Equal("Summoner", result!.Name);
    }

    [Fact]
    public async Task DetermineActiveSpellAsync_FencedJson_IsParsed()
    {
        FakeChatClient client = new()
        {
            NextText = """

                ```json
                {"spellName":"Summoner"}
                ```

                """,
        };

        List<SpellMetadata> spells = [new SpellMetadata("Summoner", "desc", "/x/SPELL.md")];

        SpellMetadata? result = await SemanticRouter.DetermineActiveSpellAsync(
            client,
            "summon",
            spells,
            TimeSpan.FromSeconds(5),
            32,
            0f,
            CancellationToken.None);

        Assert.Equal("Summoner", result!.Name);
    }

    [Fact]
    public async Task DetermineActiveSpellAsync_None_ReturnsNull()
    {
        FakeChatClient client = new()
        {
            NextText = """{"spellName":"NONE"}""",
        };

        List<SpellMetadata> spells = [new SpellMetadata("Summoner", "desc", "/x/SPELL.md")];

        SpellMetadata? result = await SemanticRouter.DetermineActiveSpellAsync(
            client,
            "unrelated",
            spells,
            TimeSpan.FromSeconds(5),
            32,
            0f,
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task DetermineActiveSpellAsync_InvalidJson_ReturnsNull()
    {
        FakeChatClient client = new()
        {
            NextText = "not json",
        };

        List<SpellMetadata> spells = [new SpellMetadata("Summoner", "desc", "/x/SPELL.md")];

        SpellMetadata? result = await SemanticRouter.DetermineActiveSpellAsync(
            client,
            "summon",
            spells,
            TimeSpan.FromSeconds(5),
            32,
            0f,
            CancellationToken.None,
            NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public async Task DetermineActiveSpellAsync_ClientThrows_ReturnsNull()
    {
        FakeChatClient client = new()
        {
            NextException = new InvalidOperationException("router down"),
        };

        List<SpellMetadata> spells = [new SpellMetadata("Summoner", "desc", "/x/SPELL.md")];

        SpellMetadata? result = await SemanticRouter.DetermineActiveSpellAsync(
            client,
            "summon",
            spells,
            TimeSpan.FromSeconds(5),
            32,
            0f,
            CancellationToken.None,
            NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public async Task DetermineActiveSpellAsync_Timeout_ReturnsNull()
    {
        FakeChatClient client = new()
        {
            Delay = TimeSpan.FromSeconds(2),
            NextText = """{"spellName":"Summoner"}""",
        };

        List<SpellMetadata> spells = [new SpellMetadata("Summoner", "desc", "/x/SPELL.md")];

        SpellMetadata? result = await SemanticRouter.DetermineActiveSpellAsync(
            client,
            "summon",
            spells,
            TimeSpan.FromMilliseconds(50),
            32,
            0f,
            CancellationToken.None,
            NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public async Task DetermineActiveSpellAsync_EscapesSingleQuotesInPrompt()
    {
        FakeChatClient client = new()
        {
            NextText = """{"spellName":"NONE"}""",
        };

        await SemanticRouter.DetermineActiveSpellAsync(
            client,
            "it's fine",
            [new SpellMetadata("A", "d", "/a/SPELL.md")],
            TimeSpan.FromSeconds(5),
            32,
            0f,
            CancellationToken.None);

        MeAiChatMessage user = client.Calls[0].Messages[0];

        Assert.Contains("it`s fine", user.Text, StringComparison.Ordinal);
    }

    private sealed class FakeChatClient : IChatClient
    {

        public List<(IReadOnlyList<MeAiChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

        public string NextText { get; init; } = string.Empty;

        public Exception? NextException { get; init; }

        public TimeSpan Delay { get; init; }

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeAiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<MeAiChatMessage> list = messages.ToList();

            Calls.Add((list, options));

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }

            if (NextException is Exception ex)
            {
                throw ex;
            }

            return new ChatResponse(new MeAiChatMessage(ChatRole.Assistant, NextText));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeAiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

}
