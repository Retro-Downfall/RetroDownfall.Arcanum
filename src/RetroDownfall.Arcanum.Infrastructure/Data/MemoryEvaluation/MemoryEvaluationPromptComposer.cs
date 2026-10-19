using System.Text;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;

public static class MemoryEvaluationPromptComposer
{
    public static Result<MemoryEvaluationPromptLayout> Compose(MemoryEvaluationValidatedRequest request, MemoryEvaluationSourceSnapshot snapshot)
    {
        try
        {
            StringBuilder system = new(request.Request.Workload.SystemPrompt);

            StringBuilder user = new(request.Request.Workload.UserPrompt);

            List<MemoryEvaluationPromptOccurrence> occurrences = [];

            UTF8Encoding utf8 = new(false, true);

            foreach (MemoryEvaluationSourceFragment fragment in snapshot.Fragments)
            {
                if (fragment.SourceIndex < 0 || fragment.SourceIndex >= snapshot.Coordinates.Length || !Enum.IsDefined(fragment.Placement))
                {
                    return new Error(ErrorCodes.MemoryEvaluation.InvalidPrompt, "A memory fragment has no exact source attribution.");
                }

                StringBuilder target = fragment.Placement == MemoryEvaluationPromptPart.System ? system : user;

                string text = "\n\n" + fragment.Text;

                occurrences.Add(new(fragment.SourceIndex, fragment.Placement, utf8.GetByteCount(target.ToString()), utf8.GetByteCount(text), snapshot.Coordinates[fragment.SourceIndex].ContentDigest));

                target.Append(text);
            }

            MemoryEvaluationPromptLayout layout = new(system.ToString(), user.ToString(), [.. occurrences]);

            Result<MemoryEvaluationPromptPair> proof = MemoryEvaluationPromptPolicy.Derive(request, snapshot, layout);

            return proof.IsSuccess ? layout : proof.Error;
        }
        catch (ArgumentException)
        {
            return new Error(ErrorCodes.MemoryEvaluation.InvalidPrompt, "A memory fragment is not strict UTF-8.");
        }
    }
}
