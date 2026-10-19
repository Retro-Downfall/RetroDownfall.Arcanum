using System.Collections.Immutable;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

/// <summary>Verifies exact UTF-8 attribution and derives control by removal from the treatment.</summary>
public static class MemoryEvaluationPromptPolicy
{
    private static readonly Error Invalid = new(ErrorCodes.MemoryEvaluation.InvalidPrompt, "The paired prompts do not prove the exact bounded intervention.");

    public static Result<MemoryEvaluationPromptPair> Derive(MemoryEvaluationValidatedRequest request, MemoryEvaluationSourceSnapshot snapshot, MemoryEvaluationPromptLayout layout)
    {
        try
        {
            if (snapshot.Coordinates.Length is < 1 or > MemoryEvaluationLimits.ManifestCoordinates
                || snapshot.Fragments.Length is < 1 or > MemoryEvaluationLimits.MaxTargets
                || layout.Occurrences.Length != snapshot.Fragments.Length
                || snapshot.Coordinates.Select(source => (source.Store, source.SubjectId, source.VersionId)).Distinct().Count() != snapshot.Coordinates.Length
                || snapshot.Fragments.Select(fragment => fragment.SourceIndex).Distinct().Count() != snapshot.Fragments.Length)
            {
                return Invalid;
            }

            var interventions = snapshot.Coordinates.Select((source, index) => (Source: source, Index: index))
                .Where(item => item.Source.Role == MemoryEvaluationCoordinateRole.Intervention).ToArray();

            if (interventions.Length != snapshot.Fragments.Length || !interventions.All(item => MatchesTarget(request.Request.Memory, item.Source)))
            {
                return Invalid;
            }

            int targetCount = request.Request.Memory.Covenant.Length + request.Request.Memory.Saga.Length + request.Request.Memory.Lexicon.Length;

            if (targetCount != interventions.Length || snapshot.RenderedMemory != string.Concat(snapshot.Fragments.Select(fragment => fragment.Text)))
            {
                return Invalid;
            }

            int memoryBytes = 0;

            HashSet<int> seen = [];

            foreach (MemoryEvaluationPromptOccurrence occurrence in layout.Occurrences)
            {
                if (!seen.Add(occurrence.SourceIndex) || occurrence.SourceIndex < 0 || occurrence.SourceIndex >= snapshot.Coordinates.Length
                    || !Enum.IsDefined(occurrence.Placement))
                {
                    return Invalid;
                }

                MemoryEvaluationSourceCoordinate source = snapshot.Coordinates[occurrence.SourceIndex];

                MemoryEvaluationSourceFragment? fragment = snapshot.Fragments.FirstOrDefault(fragment => fragment.SourceIndex == occurrence.SourceIndex);

                if (source.Role != MemoryEvaluationCoordinateRole.Intervention || fragment is null || fragment.Placement != occurrence.Placement
                    || occurrence.ContentDigest != source.ContentDigest || string.IsNullOrEmpty(fragment.Text))
                {
                    return Invalid;
                }

                byte[] expected = MemoryEvaluationValidator.StrictUtf8.GetBytes("\n\n" + fragment.Text);

                byte[] actual = MemoryEvaluationValidator.StrictUtf8.GetBytes(occurrence.Placement == MemoryEvaluationPromptPart.System ? layout.SystemPrompt : layout.UserPrompt);

                if (occurrence.StartUtf8 < 0 || occurrence.LengthUtf8 != expected.Length
                    || occurrence.StartUtf8 > actual.Length - expected.Length
                    || !actual.AsSpan(occurrence.StartUtf8, expected.Length).SequenceEqual(expected))
                {
                    return Invalid;
                }

                memoryBytes = checked(memoryBytes + expected.Length);
            }

            if (memoryBytes > MemoryEvaluationLimits.RenderedMemoryBytes
                || MemoryEvaluationValidator.StrictUtf8.GetByteCount(layout.SystemPrompt) + MemoryEvaluationValidator.StrictUtf8.GetByteCount(layout.UserPrompt) > MemoryEvaluationLimits.CompletePromptBytes)
            {
                return Invalid;
            }

            string system = Remove(layout.SystemPrompt, layout.Occurrences.Where(item => item.Placement == MemoryEvaluationPromptPart.System));

            string user = Remove(layout.UserPrompt, layout.Occurrences.Where(item => item.Placement == MemoryEvaluationPromptPart.User));

            if (system != request.Request.Workload.SystemPrompt || user != request.Request.Workload.UserPrompt)
            {
                return Invalid;
            }

            using CovenantCanonicalHashWriter writer = new();

            writer.WriteUtf8("arcanum.memory-evaluation.unchanged-spans.v1");

            writer.WriteUtf8(system);

            writer.WriteUtf8(user);

            return new MemoryEvaluationPromptPair(system, user, layout.SystemPrompt, layout.UserPrompt, layout.Occurrences, writer.FinalizeDigest());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return Invalid;
        }
    }

    private static string Remove(string text, IEnumerable<MemoryEvaluationPromptOccurrence> occurrences)
    {
        byte[] bytes = MemoryEvaluationValidator.StrictUtf8.GetBytes(text);

        using MemoryStream result = new();

        int cursor = 0;

        foreach (MemoryEvaluationPromptOccurrence occurrence in occurrences.OrderBy(item => item.StartUtf8))
        {
            if (occurrence.StartUtf8 < cursor)
            {
                throw new ArgumentException("Overlapping memory ranges.");
            }

            result.Write(bytes.AsSpan(cursor, occurrence.StartUtf8 - cursor));

            cursor = checked(occurrence.StartUtf8 + occurrence.LengthUtf8);
        }

        result.Write(bytes.AsSpan(cursor));

        return MemoryEvaluationValidator.StrictUtf8.GetString(result.ToArray());
    }

    private static bool MatchesTarget(MemoryEvaluationMemory memory, MemoryEvaluationSourceCoordinate source) => source.Store switch
    {
        MemoryEvaluationSource.Covenant => memory.Covenant.Any(target => target.EntryId.ToString("D") == source.SubjectId
            && target.VersionId.ToString("D") == source.VersionId && target.FragmentDigest == source.ContentDigest),
        MemoryEvaluationSource.Saga => memory.Saga.Any(target => target.MemoryId == source.SubjectId && target.VersionId == source.VersionId
            && (int)target.HashFormat == source.HashFormat && target.ContentDigest == source.ContentDigest),
        MemoryEvaluationSource.Lexicon => memory.Lexicon.Any(target => target.EntryId.ToString("N") == source.SubjectId && target.VersionId == source.VersionId
            && (int)target.HashFormat == source.HashFormat && target.ContentDigest == source.ContentDigest),
        _ => false
    };
}
