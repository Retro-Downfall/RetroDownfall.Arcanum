using System.Globalization;
using System.Text;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>What the operator does to a seeded memory before the sweep.</summary>
internal enum LongRestCorpusRole
{
    Ordinary = 0,

    Retired = 1,

    Pinned = 2,
}

/// <summary>
/// One fact the qualification tells the host in a seeding Session. The host's own extraction turns it into a Saga memory in the
/// model's words; the label is the ground truth the calibration compares against: facts that share a label say the same thing in
/// different words (or in the same words, for the exact duplicates), and facts of different labels do not.
/// </summary>
/// <param name="Label">The topical cluster, or <see cref="LongRestQualificationCorpus.SingleLabel"/> for an unrelated fact.</param>
/// <param name="Text">What the seeding Session says.</param>
/// <param name="Session">Which seeding Session says it (1-based). A Session never holds two facts of one label, so the extraction model cannot merge them.</param>
/// <param name="Role">Whether the operator retires or pins the memory that results.</param>
internal sealed record LongRestSeedFact(
    string Label,
    string Text,
    int Session,
    LongRestCorpusRole Role = LongRestCorpusRole.Ordinary);

/// <summary>One memory the host wrote, with the fact it came from (if the match was clear) and what the operator did to it.</summary>
internal sealed record LongRestSeededMemory(string Id, string Content, LongRestSeedFact? Fact, LongRestCorpusRole Role)
{
    public string Label => Fact?.Label ?? LongRestQualificationCorpus.UnmatchedLabel;
}

/// <summary>
/// The labelled corpus the qualification seeds through the host's own Saga extraction: topical clusters of near-paraphrases (some of
/// them confusable with a neighbour: two pets, two programming-language preferences), two exact-duplicate pairs, unrelated singles, one
/// retired memory inside a cluster and one pinned memory inside another, so that either would be a natural neighbour if the sweep did
/// not leave it out.
/// </summary>
/// <remarks>
/// The corpus is extracted rather than inserted or corrected, because the host offers no way to write a Saga memory except extraction,
/// and a memory the operator corrects no longer names its source Session in its Annals version, which Long Rest's ownership check
/// requires of a Session-owned source. What extraction writes is what the sweep reads.
/// </remarks>
internal static class LongRestQualificationCorpus
{
    public const string SingleLabel = "single";

    public const string UnmatchedLabel = "unmatched";

    /// <summary>The fewest of the told facts that must come back as memories for the qualification to proceed.</summary>
    public const double MinimumRememberedFraction = 0.8;

    public static IReadOnlyList<LongRestSeedFact> Facts { get; } =
    [
        new("dog", "The user's dog is a beagle named Biscuit.", 1),
        new("dog", "The user owns a beagle called Biscuit.", 2),
        new("dog", "Biscuit, the user's beagle, is the family dog.", 3),
        new("dog", "The user has a pet beagle whose name is Biscuit.", 4),
        new("dog", "Biscuit the beagle is the user's loyal dog and walking companion.", 5, LongRestCorpusRole.Retired),
        new("cat", "The user's cat is a grey tabby named Mochi.", 1),
        new("cat", "The user has a grey tabby cat called Mochi.", 2),
        new("cat", "Mochi is the user's grey tabby, a house cat.", 3),
        new("coffee", "The user takes their coffee black with no sugar.", 1),
        new("coffee", "The user prefers black coffee without any sugar.", 2),
        new("coffee", "The user drinks coffee black, never with sugar or milk.", 3),
        new("coffee", "Black coffee with no sugar is what the user always orders.", 4, LongRestCorpusRole.Pinned),
        new("csharp", "The user writes most of their code in C# on .NET 10.", 1),
        new("csharp", "The user's primary programming language is C#, targeting .NET 10.", 2),
        new("csharp", "The user develops mainly in C# using the .NET 10 SDK.", 3),
        new("csharp", "The user's day-to-day stack is C# and .NET 10.", 4),
        new("python", "The user avoids Python and does not enjoy writing Python scripts.", 1),
        new("python", "The user dislikes Python and prefers not to script in it.", 2),
        new("python", "Python is a language the user tries to avoid for scripting work.", 3),
        new("standup", "The user's weekly team standup is on Tuesday at 9:30 AM.", 1),
        new("standup", "The team standup meets every Tuesday at 9:30 in the morning.", 2),
        new("standup", "Every Tuesday morning at 9:30 the user attends the team standup.", 3),
        new("lisbon", "The user plans to visit Lisbon in March 2027.", 1),
        new("lisbon", "A trip to Lisbon is planned for the user in March of 2027.", 2),
        new("lisbon", "The user will travel to Lisbon next March, in 2027.", 3),
        new("frankfurt", "Production deployments go to the Frankfurt region.", 1),
        new("frankfurt", "The production environment is deployed in the Frankfurt region.", 2),
        new("frankfurt", "Releases to production are always shipped to Frankfurt.", 3),
        new("teal", "The user's favorite color is teal.", 1),
        new("teal", "The user's favorite color is teal.", 2),
        new("stagingdb", "The staging database is refreshed every Sunday night.", 1),
        new("stagingdb", "The staging database is refreshed every Sunday night.", 2),
        new(SingleLabel, "The office wifi password rotates every quarter.", 1),
        new(SingleLabel, "The user's wedding anniversary is on June 14th.", 2),
        new(SingleLabel, "The user prefers dark mode in every application.", 3),
        new(SingleLabel, "The project is released under the MIT license.", 4),
        new(SingleLabel, "The user wears size 43 shoes.", 5),
    ];

    public static int SessionCount => Facts.Max(static fact => fact.Session);

    public static IReadOnlyList<LongRestSeedFact> FactsOfSession(int session) =>
        [.. Facts.Where(fact => fact.Session == session)];

    public static LongRestSeedFact RetiredFact => Facts.Single(static fact => fact.Role == LongRestCorpusRole.Retired);

    public static LongRestSeedFact PinnedFact => Facts.Single(static fact => fact.Role == LongRestCorpusRole.Pinned);

    /// <summary>The prompt that makes one seeding Session tell the host its facts. The reply is one word, so the turn costs almost nothing.</summary>
    public static string PromptFor(int session)
    {
        StringBuilder prompt = new();

        _ = prompt.Append("Please remember each of the following facts as a separate durable fact, and just reply with the single word Noted.");

        foreach (LongRestSeedFact fact in FactsOfSession(session))
        {
            _ = prompt.Append("\n- ").Append(fact.Text);
        }

        return prompt.ToString();
    }

    /// <summary>
    /// Pairs the memories one seeding Session produced with the facts it told. A pair is the highest word overlap first, each fact and
    /// each memory at most once, and only an overlap that is plainly the same subject counts; anything else stays unmatched, so a
    /// memory the extraction model invented or merged is never labelled a cluster it does not belong to.
    /// </summary>
    public static IReadOnlyList<LongRestSeededMemory> Match(
        IReadOnlyList<(string Id, string Content)> memories,
        IReadOnlyList<LongRestSeedFact> told)
    {
        ArgumentNullException.ThrowIfNull(memories);
        ArgumentNullException.ThrowIfNull(told);

        HashSet<string>[] memoryWords = [.. memories.Select(memory => Words(memory.Content))];

        HashSet<string>[] factWords = [.. told.Select(fact => Words(fact.Text))];

        List<(double Overlap, int Memory, int Fact)> candidates = [];

        for (int memory = 0; memory < memories.Count; memory++)
        {
            for (int fact = 0; fact < told.Count; fact++)
            {
                double overlap = Overlap(memoryWords[memory], factWords[fact]);

                if (overlap >= 0.34)
                {
                    candidates.Add((overlap, memory, fact));
                }
            }
        }

        LongRestSeedFact?[] assigned = new LongRestSeedFact?[memories.Count];

        bool[] factUsed = new bool[told.Count];

        foreach ((double _, int memory, int fact) in candidates
            .OrderByDescending(static candidate => candidate.Overlap)
            .ThenBy(static candidate => candidate.Memory)
            .ThenBy(static candidate => candidate.Fact))
        {
            if (assigned[memory] is not null || factUsed[fact])
            {
                continue;
            }

            assigned[memory] = told[fact];

            factUsed[fact] = true;
        }

        return [.. memories.Select((memory, index) => new LongRestSeededMemory(memory.Id, memory.Content, assigned[index], assigned[index]?.Role ?? LongRestCorpusRole.Ordinary))];
    }

    /// <summary>
    /// The share of the shorter text's words that the other contains: a restatement keeps the subject's words, and an extraction model
    /// that adds detail still keeps all of the told fact's.
    /// </summary>
    internal static double Overlap(HashSet<string> first, HashSet<string> second)
    {
        if (first.Count == 0 || second.Count == 0)
        {
            return 0;
        }

        int shared = first.Count(second.Contains);

        return (double)shared / Math.Min(first.Count, second.Count);
    }

    internal static HashSet<string> Words(string text)
    {
        HashSet<string> words = new(StringComparer.Ordinal);

        StringBuilder word = new();

        foreach (char character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                _ = word.Append(char.ToLower(character, CultureInfo.InvariantCulture));

                continue;
            }

            Flush();
        }

        Flush();

        return words;

        void Flush()
        {
            if (word.Length > 1 && !StopWords.Contains(word.ToString()))
            {
                _ = words.Add(word.ToString());
            }

            _ = word.Clear();
        }
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "is", "are", "was", "to", "of", "in", "on", "an", "and", "for", "at", "by", "it", "its", "as", "with", "that", "this",
        "user", "users", "their", "they", "has", "have", "be", "or", "from", "his", "her",
    };
}
