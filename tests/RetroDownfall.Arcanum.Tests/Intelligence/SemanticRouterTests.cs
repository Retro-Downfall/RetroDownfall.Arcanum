using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class SemanticRouterTests
{

    [Fact]
    public async Task DetermineActiveSpellAsync_EmptyCatalog_ReturnsNull()
    {
        FakeChatClient client = new();

        SemanticSpellRoutingResult? result = await SemanticRouter.DetermineActiveSpellAsync(
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
            NextText = """{"spellName":"Summoner","entities":["Alice"]}""",
        };

        List<SpellMetadata> spells =
        [
            new SpellMetadata("Summoner", "summon things", "/spells/summoner/SPELL.md"),
            new SpellMetadata("Other", "other", "/spells/other/SPELL.md"),
        ];

        SemanticSpellRoutingResult? result = await SemanticRouter.DetermineActiveSpellAsync(
            client,
            "please summon",
            spells,
            TimeSpan.FromSeconds(5),
            maxOutputTokens: 32,
            temperature: 0f,
            CancellationToken.None);

        Assert.NotNull(result);

        Assert.Equal("Summoner", result!.Spell!.Name);

        Assert.Single(result.Entities);

        Assert.Equal("Alice", result.Entities[0]);
    }

    [Fact]
    public async Task DetermineActiveSpellAsync_FencedJson_IsParsed()
    {
        FakeChatClient client = new()
        {
            NextText = """

                ```json
                {"spellName":"Summoner","entities":[]}
                ```

                """,
        };

        List<SpellMetadata> spells = [new SpellMetadata("Summoner", "desc", "/x/SPELL.md")];

        SemanticSpellRoutingResult? result = await SemanticRouter.DetermineActiveSpellAsync(
            client,
            "summon",
            spells,
            TimeSpan.FromSeconds(5),
            32,
            0f,
            CancellationToken.None);

        Assert.Equal("Summoner", result!.Spell!.Name);

        Assert.Empty(result.Entities);
    }

    [Fact]
    public async Task DetermineActiveSpellAsync_None_StillReturnsEntities()
    {
        FakeChatClient client = new()
        {
            NextText = """{"spellName":"NONE","entities":["Project Phoenix"]}""",
        };

        List<SpellMetadata> spells = [new SpellMetadata("Summoner", "desc", "/x/SPELL.md")];

        SemanticSpellRoutingResult? result = await SemanticRouter.DetermineActiveSpellAsync(
            client,
            "unrelated",
            spells,
            TimeSpan.FromSeconds(5),
            32,
            0f,
            CancellationToken.None);

        Assert.NotNull(result);

        Assert.Null(result!.Spell);

        Assert.Single(result.Entities);

        Assert.Equal("Project Phoenix", result.Entities[0]);
    }

    [Fact]
    public async Task DetermineActiveSpellAsync_MissingEntities_ReturnsEmpty()
    {
        FakeChatClient client = new()
        {
            NextText = """{"spellName":"Summoner"}""",
        };

        List<SpellMetadata> spells = [new SpellMetadata("Summoner", "desc", "/x/SPELL.md")];

        SemanticSpellRoutingResult? result = await SemanticRouter.DetermineActiveSpellAsync(
            client,
            "summon",
            spells,
            TimeSpan.FromSeconds(5),
            32,
            0f,
            CancellationToken.None);

        Assert.Equal("Summoner", result!.Spell!.Name);

        Assert.Empty(result.Entities);
    }

    [Fact]
    public async Task DetermineActiveSpellAsync_InvalidJson_ReturnsEmptyOutcome()
    {
        FakeChatClient client = new()
        {
            NextText = "not json",
        };

        List<SpellMetadata> spells = [new SpellMetadata("Summoner", "desc", "/x/SPELL.md")];

        SemanticSpellRoutingResult? result = await SemanticRouter.DetermineActiveSpellAsync(
            client,
            "summon",
            spells,
            TimeSpan.FromSeconds(5),
            32,
            0f,
            CancellationToken.None,
            NullLogger.Instance);

        Assert.NotNull(result);
        Assert.Null(result.Spell);
        Assert.Empty(result.Entities);
    }

    [Fact]
    public async Task DetermineActiveSpellAsync_ClientThrows_ReturnsNull()
    {
        FakeChatClient client = new()
        {
            NextException = new InvalidOperationException("router down"),
        };

        List<SpellMetadata> spells = [new SpellMetadata("Summoner", "desc", "/x/SPELL.md")];

        SemanticSpellRoutingResult? result = await SemanticRouter.DetermineActiveSpellAsync(
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
            NextText = """{"spellName":"Summoner","entities":[]}""",
        };

        List<SpellMetadata> spells = [new SpellMetadata("Summoner", "desc", "/x/SPELL.md")];

        SemanticSpellRoutingResult? result = await SemanticRouter.DetermineActiveSpellAsync(
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
            NextText = """{"spellName":"NONE","entities":[]}""",
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

    [Fact]
    public async Task DetermineActiveSpellAsync_NullCandidates_OffersFullCatalogToLlm()
    {
        FakeChatClient client = new()
        {
            NextText = """{"spellName":"NONE","entities":[]}""",
        };

        List<SpellMetadata> spells =
        [
            new SpellMetadata("Alpha", "alpha desc", "/a/SPELL.md"),
            new SpellMetadata("Beta", "beta desc", "/b/SPELL.md"),
        ];

        await SemanticRouter.DetermineActiveSpellAsync(
            client,
            "any prompt",
            spells,
            TimeSpan.FromSeconds(5),
            32,
            0f,
            CancellationToken.None,
            candidates: null);

        MeAiChatMessage user = client.Calls[0].Messages[0];

        Assert.Contains("Alpha", user.Text, StringComparison.Ordinal);

        Assert.Contains("Beta", user.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetermineActiveSpellAsync_NonNullCandidates_OffersOnlyCandidatesToLlm()
    {
        FakeChatClient client = new()
        {
            NextText = """{"spellName":"Alpha","entities":[]}""",
        };

        List<SpellMetadata> spells =
        [
            new SpellMetadata("Alpha", "alpha desc", "/a/SPELL.md"),
            new SpellMetadata("Beta", "beta desc", "/b/SPELL.md"),
            new SpellMetadata("Gamma", "gamma desc", "/g/SPELL.md"),
        ];

        List<SpellMetadata> candidates = [spells[0]];

        SemanticSpellRoutingResult? result = await SemanticRouter.DetermineActiveSpellAsync(
            client,
            "any prompt",
            spells,
            TimeSpan.FromSeconds(5),
            32,
            0f,
            CancellationToken.None,
            candidates: candidates);

        MeAiChatMessage user = client.Calls[0].Messages[0];

        Assert.Contains("Alpha", user.Text, StringComparison.Ordinal);

        Assert.DoesNotContain("Beta", user.Text, StringComparison.Ordinal);

        Assert.DoesNotContain("Gamma", user.Text, StringComparison.Ordinal);

        // Name resolution runs against the offered candidate set, so a match within that set still
        // resolves correctly.
        Assert.Equal("Alpha", result!.Spell!.Name);
    }

    [Fact]
    public async Task DetermineActiveSpellAsync_ResponseNamesSpellOutsideCandidates_ReturnsNullSpell()
    {

        // A hallucinated (or otherwise out-of-set) response naming a real spell that exists in the
        // full catalog but was never offered to the LLM (not in `candidates`) must not resolve —
        // otherwise the whole point of the top-K candidate filter would be silently defeated.
        FakeChatClient client = new()
        {
            NextText = """{"spellName":"Gamma","entities":["foo"]}""",
        };

        List<SpellMetadata> spells =
        [
            new SpellMetadata("Alpha", "alpha desc", "/a/SPELL.md"),
            new SpellMetadata("Beta", "beta desc", "/b/SPELL.md"),
            new SpellMetadata("Gamma", "gamma desc", "/g/SPELL.md"),
        ];

        List<SpellMetadata> candidates = [spells[0], spells[1]];

        SemanticSpellRoutingResult? result = await SemanticRouter.DetermineActiveSpellAsync(
            client,
            "any prompt",
            spells,
            TimeSpan.FromSeconds(5),
            32,
            0f,
            CancellationToken.None,
            candidates: candidates);

        Assert.NotNull(result);

        Assert.Null(result!.Spell);

    }

    [Fact]
    public async Task DetermineActiveSpellAsync_EmptyCandidatesList_ReturnsNullWithoutCallingLlm()
    {
        FakeChatClient client = new();

        List<SpellMetadata> spells = [new SpellMetadata("Alpha", "alpha desc", "/a/SPELL.md")];

        SemanticSpellRoutingResult? result = await SemanticRouter.DetermineActiveSpellAsync(
            client,
            "any prompt",
            spells,
            TimeSpan.FromSeconds(5),
            32,
            0f,
            CancellationToken.None,
            candidates: []);

        Assert.Null(result);

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task DetermineActiveSpellAsync_preserves_every_distinct_extracted_entity()
    {
        List<string> many = Enumerable.Range(0, 12).Select(i => i % 2 == 0 ? $"entity{i}" : $"Entity{i}").ToList();

        string entitiesJson = string.Join(",", many.Select(e => $"\"{e}\""));

        FakeChatClient client = new()
        {
            NextText = $$"""{"spellName":"NONE","entities":[{{entitiesJson}}]}""",
        };

        List<SpellMetadata> spells = [new SpellMetadata("A", "d", "/a/SPELL.md")];

        SemanticSpellRoutingResult? result = await SemanticRouter.DetermineActiveSpellAsync(
            client,
            "prompt",
            spells,
            TimeSpan.FromSeconds(5),
            32,
            0f,
            CancellationToken.None);

        Assert.Equal(many, result!.Entities);
    }

    [Fact]
    public async Task LexiconEntityExtractor_ExtractsEntitiesFromJson()
    {
        FakeChatClient client = new()
        {
            NextText = """{"entities":["Alice","Project Phoenix"]}""",
        };

        (IReadOnlyList<string> entities, _) = await LexiconEntityExtractor.ExtractAsync(
            client,
            "tell me about Alice and Project Phoenix",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(2, entities.Count);

        Assert.Contains("Alice", entities);
    }

    [Fact]
    public async Task LexiconEntityExtractor_InvalidJson_ReturnsEmpty()
    {
        FakeChatClient client = new()
        {
            NextText = "not json",
        };

        (IReadOnlyList<string> entities, _) = await LexiconEntityExtractor.ExtractAsync(
            client,
            "prompt",
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            NullLogger.Instance);

        Assert.Empty(entities);
    }

    [Fact]
    public async Task LexiconEntityExtractor_EmptyPrompt_ReturnsEmptyWithoutCallingLlm()
    {
        FakeChatClient client = new();

        (IReadOnlyList<string> entities, _) = await LexiconEntityExtractor.ExtractAsync(
            client,
            "   ",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Empty(entities);

        Assert.Empty(client.Calls);
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
