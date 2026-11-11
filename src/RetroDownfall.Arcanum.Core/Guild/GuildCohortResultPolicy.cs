using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

internal sealed record GuildCohortVote(
    Guid MemberId,
    long AdmissionSequence,
    long? ResultSequence,
    GuildContributionOutcome? Outcome,
    string? AgreementDigest,
    ReadOnlyMemory<byte> ResultUtf8);

internal sealed record GuildCohortPolicyDecision(
    GuildConvergenceDecision? Decision,
    ImmutableArray<Guid> SelectedMemberIds,
    long PendingMemberCount);

internal static class GuildCohortResultPolicy
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Checks the exact designated value after the caller verifies primary-Quest completion,
    /// result ownership and sensitivity authorization. No participant digest is accepted.
    /// </summary>
    public static Result<string> ValidateResult(GuildResultContract contract, string outputMimeType,
        ReadOnlyMemory<byte> resultUtf8, int designatedResultCount = 1)
    {
        if (contract is null || contract.MimeType is not ("text/plain" or "application/json")
            || outputMimeType != contract.MimeType || designatedResultCount != 1)
        {
            return Result<string>.Failure(Invalid("The final result must match the supported frozen MIME contract and designate exactly one value."));
        }

        try
        {
            if (contract.MimeType == "text/plain")
            {
                if (string.IsNullOrWhiteSpace(StrictUtf8.GetString(resultUtf8.Span)))
                {
                    return Result<string>.Failure(Invalid("The final text result must not be blank."));
                }
            }
            else
            {
                _ = StrictUtf8.GetCharCount(resultUtf8.Span);

                if (!IsJsonObjectOrArray(resultUtf8.Span))
                {
                    return Result<string>.Failure(Invalid("The final JSON result must be one complete object or array without duplicate property names."));
                }
            }
        }
        catch (Exception exception) when (exception is DecoderFallbackException or JsonException or InvalidOperationException)
        {
            return Result<string>.Failure(Invalid("The final result has malformed UTF-8 or JSON content."));
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData("guild-result-v1\n"u8);

        hash.AppendData(Encoding.ASCII.GetBytes(contract.MimeType));

        hash.AppendData([0]);

        hash.AppendData(resultUtf8.Span);

        return Result<string>.Success(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>
    /// Evaluates one host-verified membership snapshot. Null outcomes are pending; terminal
    /// accepted votes contain exact immutable result bytes and their host-computed digest.
    /// Open membership never produces a decision. The caller durably retains the first
    /// decision and must not evaluate late receipts as a replacement decision.
    /// </summary>
    public static Result<GuildCohortPolicyDecision> Evaluate(bool membershipClosed, GuildConvergencePolicy policy,
        ImmutableArray<GuildCohortVote> members)
    {
        if (policy is null || !Enum.IsDefined(policy.Kind) || members.IsDefault
            || (policy.Kind == GuildConvergenceKind.Quorum ? policy.Quorum is null or < 1 : policy.Quorum is not null)
            || membershipClosed && (members.IsEmpty || policy.Quorum > members.Length))
        {
            return Result<GuildCohortPolicyDecision>.Failure(Invalid("The convergence policy or closed membership is invalid."));
        }

        HashSet<Guid> identities = [];

        HashSet<long> admissions = [];

        HashSet<long> results = [];

        long pending = 0;

        foreach (GuildCohortVote member in members)
        {
            if (member is null || member.MemberId == Guid.Empty || member.AdmissionSequence < 1
                || !identities.Add(member.MemberId) || !admissions.Add(member.AdmissionSequence)
                || member.Outcome is { } outcome && !Enum.IsDefined(outcome)
                || member.Outcome is null != (member.ResultSequence is null)
                || member.ResultSequence is { } sequence && (sequence < 1 || !results.Add(sequence))
                || member.Outcome == GuildContributionOutcome.Accepted
                    && (string.IsNullOrWhiteSpace(member.AgreementDigest) || member.ResultUtf8.IsEmpty))
            {
                return Result<GuildCohortPolicyDecision>.Failure(Invalid("Membership and terminal contribution identities must be complete and unique."));
            }

            if (member.Outcome is null)
            {
                pending++;
            }
        }

        GuildCohortPolicyDecision Waiting() => new(null, [], pending);

        GuildCohortPolicyDecision Impossible() => new(GuildConvergenceDecision.Impossible, [], pending);

        GuildCohortPolicyDecision Success(IEnumerable<GuildCohortVote> selected) =>
            new(GuildConvergenceDecision.Success, selected.Select(member => member.MemberId).ToImmutableArray(), pending);

        if (!membershipClosed)
        {
            return Result<GuildCohortPolicyDecision>.Success(Waiting());
        }

        if (policy.Kind == GuildConvergenceKind.All)
        {
            return Result<GuildCohortPolicyDecision>.Success(members.Any(member => member.Outcome is not (null or GuildContributionOutcome.Accepted))
                ? Impossible()
                : pending > 0 ? Waiting() : Success(members.OrderBy(member => member.AdmissionSequence)));
        }

        GuildCohortVote[] accepted = members.Where(member => member.Outcome == GuildContributionOutcome.Accepted)
            .OrderBy(member => member.ResultSequence).ToArray();

        if (policy.Kind == GuildConvergenceKind.First)
        {
            return Result<GuildCohortPolicyDecision>.Success(accepted.Length > 0
                ? Success(accepted.Take(1)) : pending > 0 ? Waiting() : Impossible());
        }

        Dictionary<string, List<List<GuildCohortVote>>> digestBuckets = new(StringComparer.Ordinal);

        int largest = 0;

        foreach (GuildCohortVote member in accepted)
        {
            if (!digestBuckets.TryGetValue(member.AgreementDigest!, out List<List<GuildCohortVote>>? collisions))
            {
                collisions = [];

                digestBuckets.Add(member.AgreementDigest!, collisions);
            }

            List<GuildCohortVote>? bucket = collisions.Find(candidate =>
                candidate[0].ResultUtf8.Length == member.ResultUtf8.Length
                && candidate[0].ResultUtf8.Span.SequenceEqual(member.ResultUtf8.Span));

            if (bucket is null)
            {
                bucket = [];

                collisions.Add(bucket);
            }

            bucket.Add(member);

            if (bucket.Count == policy.Quorum)
            {
                return Result<GuildCohortPolicyDecision>.Success(Success(bucket));
            }

            largest = Math.Max(largest, bucket.Count);
        }

        return Result<GuildCohortPolicyDecision>.Success(largest + pending < policy.Quorum ? Impossible() : Waiting());
    }

    private static bool IsJsonObjectOrArray(ReadOnlySpan<byte> utf8)
    {
        Utf8JsonReader reader = new(utf8, new JsonReaderOptions { MaxDepth = int.MaxValue });

        if (!reader.Read() || reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
        {
            return false;
        }

        Stack<HashSet<string>> properties = new();

        do
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    properties.Push(new(StringComparer.Ordinal));

                    break;

                case JsonTokenType.EndObject:
                    properties.Pop();

                    break;

                case JsonTokenType.PropertyName:
                    if (!properties.Peek().Add(reader.GetString()!))
                    {
                        return false;
                    }

                    break;

                case JsonTokenType.String:
                    _ = reader.GetString();

                    break;
            }
        } while (reader.Read());

        return reader.CurrentDepth == 0;
    }

    private static Error Invalid(string message) => new(ErrorCodes.Guild.InvalidRequest, message);
}
