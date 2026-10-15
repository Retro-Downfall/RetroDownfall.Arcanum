using System.Collections.Immutable;
using System.Text.Json;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>
/// Reads the model's member verdicts for one adjudication request (<see cref="LongRestPromptLayout.DecisionSchema"/>) into the decision the
/// sweep acts on. For each numbered memory the model restates its fact and lists the earlier memories it states the same fact as. The unit is
/// decided as a whole: it is <see cref="LongRestAdjudicationKind.EquivalentObservations"/> only when every pair of its memories is asserted
/// the same and Number Agreement holds (<see cref="LongRestPairing.NumbersAgree(ImmutableArray{LongRestSourceLeaf})"/>), and otherwise
/// <see cref="LongRestAdjudicationKind.NoChange"/>. The model path never produces a supersession and never names a survivor, so there is no
/// identifier for the model to echo wrongly and no label that lets one real duplicate drag a whole pack in. Pure.
/// </summary>
public static class LongRestMemberVerdict
{
    /// <summary>
    /// The decision for <paramref name="answerJson"/>, or null when the answer is not a well-formed instance of the closed shape (the caller
    /// treats it as an invalid paid answer). A well-formed answer never fails: a reference to a memory that does not exist, a memory that refers
    /// to itself, and a memory listed twice contribute nothing. The restated facts are read and discarded; nothing here keeps or returns them.
    /// </summary>
    public static LongRestAdjudicationDecision? Decide(string answerJson, ImmutableArray<LongRestSourceLeaf> inputs)
    {
        if (answerJson is null || inputs.IsDefaultOrEmpty)
        {
            return null;
        }

        int count = inputs.Length;

        try
        {
            using JsonDocument document = JsonDocument.Parse(answerJson);

            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !ExactlyProperties(root, "memories") || root.GetProperty("memories").ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            JsonElement memories = root.GetProperty("memories");

            // Shape first, so that a malformed entry is invalid whatever else the answer says.
            foreach (JsonElement entry in memories.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !ExactlyProperties(entry, "memory", "fact", "sameAs")
                    || !entry.GetProperty("memory").TryGetInt32(out _) || entry.GetProperty("fact").ValueKind != JsonValueKind.String
                    || entry.GetProperty("sameAs").ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (JsonElement reference in entry.GetProperty("sameAs").EnumerateArray())
                {
                    if (!reference.TryGetInt32(out _))
                    {
                        return null;
                    }
                }
            }

            int[] listed = new int[count + 1];

            foreach (JsonElement entry in memories.EnumerateArray())
            {
                int memory = entry.GetProperty("memory").GetInt32();

                if (memory >= 1 && memory <= count)
                {
                    listed[memory]++;
                }
            }

            bool[,] asserted = new bool[count + 1, count + 1];

            foreach (JsonElement entry in memories.EnumerateArray())
            {
                int memory = entry.GetProperty("memory").GetInt32();

                if (memory < 1 || memory > count || listed[memory] != 1)
                {
                    continue;
                }

                foreach (JsonElement reference in entry.GetProperty("sameAs").EnumerateArray())
                {
                    int other = reference.GetInt32();

                    if (other < 1 || other > count || other == memory)
                    {
                        continue;
                    }

                    asserted[memory, other] = true;

                    asserted[other, memory] = true;
                }
            }

            for (int left = 1; left <= count; left++)
            {
                for (int right = left + 1; right <= count; right++)
                {
                    if (!asserted[left, right])
                    {
                        return NoChange;
                    }
                }
            }

            return count >= 2 && LongRestPairing.NumbersAgree(inputs) ? new(LongRestAdjudicationKind.EquivalentObservations, null) : NoChange;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static LongRestAdjudicationDecision NoChange => new(LongRestAdjudicationKind.NoChange, null);

    // The closed shape: exactly these properties, each once, and no other.
    private static bool ExactlyProperties(JsonElement element, params string[] names)
    {
        int seen = 0;

        foreach (JsonProperty property in element.EnumerateObject())
        {
            bool known = false;

            for (int index = 0; index < names.Length; index++)
            {
                known |= string.Equals(property.Name, names[index], StringComparison.Ordinal);
            }

            if (!known)
            {
                return false;
            }

            seen++;
        }

        if (seen != names.Length)
        {
            return false;
        }

        // Each name was matched by exactly one property: the count is right and no property is repeated.
        for (int index = 0; index < names.Length; index++)
        {
            int matches = 0;

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, names[index], StringComparison.Ordinal))
                {
                    matches++;
                }
            }

            if (matches != 1)
            {
                return false;
            }
        }

        return true;
    }
}
