using System.Diagnostics;
using System.Globalization;

namespace RetroDownfall.Arcanum.Tests.Packaging;

internal sealed partial class LongRestQualificationRun
{
    private static readonly TimeSpan ExtractionQuietWindow = TimeSpan.FromSeconds(12);

    private static readonly TimeSpan ExtractionBudget = TimeSpan.FromMinutes(8);

    /// <summary>The seeded corpus, labelled, after the operator retired one memory and pinned another.</summary>
    public IReadOnlyList<LongRestSeededMemory> Corpus => _corpus;

    /// <summary>
    /// Tells the host the corpus's facts, one Session at a time, so that its own Saga extraction writes them as memories; then
    /// retires one memory and pins another through the API. No Session holds two facts of one cluster, so the extraction model keeps
    /// them apart, and the Sessions run one after another so that each one's memories are known by the Session that wrote them.
    /// </summary>
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();

        HashSet<Guid> knownSessions = [];

        List<LongRestSeededMemory> corpus = [];

        int told = 0;

        for (int session = 1; session <= LongRestQualificationCorpus.SessionCount; session++)
        {
            IReadOnlyList<LongRestSeedFact> facts = LongRestQualificationCorpus.FactsOfSession(session);

            int before = await Api.SagaCountAsync(cancellationToken);

            _ = await LongRestPublishedCli.RunAsync(
                _options.Executable,
                Installation,
                TurnBudget,
                "--plain",
                "--print",
                "--no-context",
                "run",
                "--new",
                "--model",
                _options.ChatModel,
                "--temperature",
                "0",
                "--max-tokens",
                "64",
                "--unattended",
                LongRestQualificationCorpus.PromptFor(session));

            await WaitForExtractionAsync(before, cancellationToken);

            IReadOnlyList<LongRestApiMemory> memories = await Api.ListMemoriesAsync(cancellationToken);

            List<LongRestApiMemory> fresh =
            [
                .. memories.Where(memory => memory.SessionId is { } owner && !knownSessions.Contains(owner)),
            ];

            Guid[] owners = [.. fresh.Select(static memory => memory.SessionId!.Value).Distinct()];

            if (owners.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Seeding Session {session} left memories from {owners.Length} unknown Sessions; the corpus cannot be attributed.");
            }

            if (owners.Length == 1)
            {
                _ = knownSessions.Add(owners[0]);
            }

            IReadOnlyList<LongRestSeededMemory> matched = LongRestQualificationCorpus.Match(
                [.. fresh.Select(static memory => (memory.Id, memory.Content))],
                facts);

            corpus.AddRange(matched);

            told += facts.Count;

            _log.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"Seeding Session {session}/{LongRestQualificationCorpus.SessionCount}: told {facts.Count} facts, extraction wrote {fresh.Count} memories, {matched.Count(static memory => memory.Fact is not null)} matched a fact"));
        }

        int remembered = corpus.Count(static memory => memory.Fact is not null);

        Assert.True(
            remembered >= (int)Math.Ceiling(told * LongRestQualificationCorpus.MinimumRememberedFraction),
            $"Extraction wrote {remembered} memories that match the {told} told facts; the labelled corpus needs at least {LongRestQualificationCorpus.MinimumRememberedFraction:P0}. "
            + "This is the harness's seeding model, not Long Rest.");

        corpus = [.. await ApplyRolesAsync(corpus, cancellationToken)];

        _corpus = corpus;

        Observe(string.Create(
            CultureInfo.InvariantCulture,
            $"Corpus: {corpus.Count} memories ({remembered} matched to told facts), {corpus.Select(static memory => memory.Label).Where(static label => label is not (LongRestQualificationCorpus.SingleLabel or LongRestQualificationCorpus.UnmatchedLabel)).Distinct().Count()} labelled clusters, "
            + $"{corpus.Count(static memory => memory.Role == LongRestCorpusRole.Retired)} retired, {corpus.Count(static memory => memory.Role == LongRestCorpusRole.Pinned)} pinned; seeding took {Took(clock)}"));
    }

    /// <summary>
    /// Waits for the host's extraction to finish writing one Session's memories: the count has grown and then been still for a quiet
    /// window, because extraction runs after the turn's reply and writes its conclusions one at a time.
    /// </summary>
    private async Task WaitForExtractionAsync(int before, CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();

        Stopwatch sinceChange = Stopwatch.StartNew();

        int last = before;

        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

            int count = await Api.SagaCountAsync(cancellationToken);

            if (count != last)
            {
                last = count;

                sinceChange.Restart();
            }

            if (last > before && sinceChange.Elapsed >= ExtractionQuietWindow)
            {
                return;
            }

            if (clock.Elapsed >= ExtractionBudget)
            {
                throw new TimeoutException(
                    $"The host's Saga extraction wrote no memories within {ExtractionBudget} (count {last}, started at {before}). "
                    + "This is the harness's seeding model, not Long Rest.");
            }
        }
    }

    /// <summary>Retires one memory of the retired fact's cluster and pins one of the pinned fact's, preferring the very memory told for it.</summary>
    private async Task<IReadOnlyList<LongRestSeededMemory>> ApplyRolesAsync(
        IReadOnlyList<LongRestSeededMemory> corpus,
        CancellationToken cancellationToken)
    {
        List<LongRestSeededMemory> roles = [.. corpus];

        string retiredId = ChooseForRole(roles, LongRestQualificationCorpus.RetiredFact, LongRestCorpusRole.Retired);

        string pinnedId = ChooseForRole(roles, LongRestQualificationCorpus.PinnedFact, LongRestCorpusRole.Pinned);

        await Api.RetireAsync(retiredId, cancellationToken);

        await Api.PinAsync(pinnedId, cancellationToken);

        _log.Info("Operator retired one memory and pinned another through the API");

        return roles;
    }

    private static string ChooseForRole(List<LongRestSeededMemory> roles, LongRestSeedFact designated, LongRestCorpusRole role)
    {
        int index = roles.FindIndex(memory => ReferenceEquals(memory.Fact, designated));

        if (index < 0)
        {
            // The extraction model did not return the memory told for this role; any other member of the cluster serves.
            index = roles.FindIndex(memory => memory.Fact is { } fact && fact.Label == designated.Label && memory.Role == LongRestCorpusRole.Ordinary);
        }

        Assert.True(index >= 0, $"No memory of the '{designated.Label}' cluster exists to be {role}; the extraction did not write that cluster.");

        string content = roles[index].Content;

        Assert.True(
            roles.Count(memory => memory.Content.Contains(content, StringComparison.OrdinalIgnoreCase)) == 1,
            $"The memory chosen to be {role} shares its text with another memory, so its being sent could not be told apart.");

        roles[index] = roles[index] with { Role = role };

        return roles[index].Id;
    }
}
