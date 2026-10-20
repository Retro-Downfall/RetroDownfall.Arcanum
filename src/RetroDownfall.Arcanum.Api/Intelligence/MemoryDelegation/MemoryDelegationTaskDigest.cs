using System.Globalization;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;

namespace RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;

/// <summary>Hashes frozen executed values, including attachment identities before DTO conversion loses them.</summary>
internal static class MemoryDelegationTaskDigest
{
    internal static CovenantDigest Compute(SubagentRunRequest request, IReadOnlyList<Guid?> attachmentIds)
    {
        using CovenantCanonicalHashWriter writer = new();

        writer.WriteUtf8("arcanum.memory-delegation.child-task.v1");

        writer.WriteInt32((int)MemoryDelegationPurpose.DelegatedTask);

        writer.WriteUtf8(request.Prompt);

        writer.WriteOptionalReference(request.Model, static (target, value) => target.WriteUtf8(value));

        writer.WriteCount((ulong)request.Files.Count);

        if (attachmentIds.Count != request.Files.Count) throw new ArgumentException("Attachment identities must match executed files.");

        for (int index = 0; index < request.Files.Count; index++)
        {
            writer.WriteUtf8(request.Files[index].RelativePath);

            writer.WriteUtf8(request.Files[index].Content);

            writer.WriteOptional(attachmentIds[index], static (target, id) => target.WriteGuid(id));
        }

        writer.WriteOptional(request.MaxTokens, static (target, value) => target.WriteInt64(value));

        writer.WriteOptional(request.MaxCostUsd, static (target, value) =>
        {
            // G29 removes insignificant decimal scale; the four fixed-width words encode the exact numeric value.
            decimal canonical = decimal.Parse(value.ToString("G29", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);

            foreach (int word in decimal.GetBits(canonical)) target.WriteInt32(word);
        });

        return writer.FinalizeDigest();
    }
}
