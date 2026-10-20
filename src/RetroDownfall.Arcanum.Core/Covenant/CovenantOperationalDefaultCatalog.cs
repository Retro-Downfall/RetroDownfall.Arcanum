using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>The closed nonsecurity preferences an approved inference consumer may apply.</summary>
public static class CovenantOperationalDefaultCatalog
{
    public const uint PolicyVersion = 1;

    public const string TemperatureKey = "defaults.inference.temperature";

    public const string TopPKey = "defaults.inference.top-p";

    /// <summary>Selects available defaults only from this attempt's exact admitted Confirmed sources.</summary>
    public static ImmutableArray<CovenantOperationalDefault> SelectAdmitted(
        CovenantTurnPlan plan,
        CovenantAdmissionPlan admission)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(admission);

        return SelectAdmitted(plan, admission.Candidates, admission.ConfirmedAdmitted);
    }

    internal static ImmutableArray<CovenantOperationalDefault> SelectAdmitted(
        CovenantTurnPlan plan,
        ImmutableArray<CovenantAdmissionCandidateDecision> candidates,
        bool confirmedAdmitted)
    {
        if (candidates.IsDefault || candidates.Length != plan.EligibleDecisions.Length)
        {
            throw new ArgumentException("Operational defaults require the exact eligible admission vector.", nameof(candidates));
        }

        for (int index = 0; index < candidates.Length; index++)
        {
            CovenantAdmissionCandidateDecision admitted = candidates[index];
            CovenantPlanCandidateDecision eligible = plan.EligibleDecisions[index];

            if (admitted.EntryId != eligible.Candidate.EntryId || admitted.VersionId != eligible.Candidate.VersionId)
            {
                throw new ArgumentException("Operational default admission must preserve exact source identities and order.", nameof(candidates));
            }

            if (eligible.Decision == CovenantPlanDecision.EligibleConfirmed
                && admitted.Decision != (confirmedAdmitted ? CovenantAdmissionDecision.Admitted : CovenantAdmissionDecision.RequiredNoFit))
            {
                throw new ArgumentException("Operational defaults require consistent Confirmed admission.", nameof(candidates));
            }
        }

        return confirmedAdmitted ? plan.OperationalDefaults : [];
    }

    internal static ImmutableArray<CovenantOperationalDefault> FromDecisions(
        ImmutableArray<CovenantPlanCandidateDecision> decisions)
    {
        ImmutableArray<CovenantOperationalDefault>.Builder defaults = ImmutableArray.CreateBuilder<CovenantOperationalDefault>();

        foreach (CovenantPlanCandidateDecision decision in decisions)
        {
            CovenantSnapshotCandidate candidate = decision.Candidate;
            string key = candidate.NormalizedKey.Value;

            if (decision.Decision != CovenantPlanDecision.EligibleConfirmed
                || candidate.Lane != CovenantLane.Confirmed
                || candidate.Operation != CovenantOperation.Set
                || candidate.Integrity != CovenantSnapshotCandidateIntegrity.Verified
                || candidate.CompilerPolicy != CovenantCompiler.CompilerPolicyVersion
                || candidate.RendererPolicy != CovenantCompiler.RendererPolicyVersion
                || candidate.Origin is not (CovenantOrigin.Operator or CovenantOrigin.AgentApproved)
                || !key.StartsWith("defaults.", StringComparison.Ordinal))
            {
                continue;
            }

            defaults.Add(new CovenantOperationalDefault(
                key == TemperatureKey ? CovenantOperationalDefaultKind.Temperature : CovenantOperationalDefaultKind.TopP,
                CovenantOperationalDefaultConsumer.InferenceSampling,
                ParseFragment(candidate),
                PolicyVersion,
                candidate.EntryId,
                candidate.VersionId,
                candidate.Revision,
                candidate.Scope,
                candidate.CampaignId));
        }

        return defaults.ToImmutable();
    }

    /// <summary>
    /// Validates reserved in-scope Confirmed heads before precedence can hide a damaged artifact.
    /// Scope, lane, integrity and compiler/renderer policy remain the linker's admission checks.
    /// </summary>
    internal static void ValidateAuthoritativeFragment(CovenantSnapshotCandidate candidate)
    {
        if (candidate.NormalizedKey.Value.StartsWith("defaults.", StringComparison.Ordinal))
        {
            _ = ParseFragment(candidate);
        }
    }

    private static float ParseFragment(CovenantSnapshotCandidate candidate)
    {
        try
        {
            string key = candidate.NormalizedKey.Value;
            string fragment = Encoding.UTF8.GetString(candidate.CompiledFragment.AsSpan());
            string prefix = $"- {key}: \"";

            if (!fragment.StartsWith(prefix, StringComparison.Ordinal)
                || !fragment.EndsWith("\"\n", StringComparison.Ordinal)
                || fragment.Length < prefix.Length + 2)
            {
                throw InvalidValue();
            }

            string scalar = fragment.Substring(prefix.Length, fragment.Length - prefix.Length - 2);

            return ParseScalar(scalar, Maximum(key));
        }
        catch (ArgumentException)
        {
            throw new CovenantOperationalDefaultInvalidException();
        }
    }

    internal static void ValidateAuthored(string key, string content)
    {
        if (!key.StartsWith("defaults.", StringComparison.Ordinal))
        {
            return;
        }

        _ = ParseScalar(content, Maximum(key));
    }

    private static int Maximum(string key) =>
        key switch
        {
            TemperatureKey => 2,
            TopPKey => 1,
            _ => throw new ArgumentException("The Covenant operational default kind or consumer is unsupported.", nameof(key))
        };

    private static float ParseScalar(string content, int maximum)
    {
        if (content.Length == 0 || content[0] < '0' || content[0] > '0' + maximum)
        {
            throw InvalidValue();
        }

        if (content.Length > 1)
        {
            if (content.Length < 3 || content[1] != '.')
            {
                throw InvalidValue();
            }

            for (int index = 2; index < content.Length; index++)
            {
                char digit = content[index];

                if (digit is < '0' or > '9' || content[0] == '0' + maximum && digit != '0')
                {
                    throw InvalidValue();
                }
            }
        }

        if (!float.TryParse(content, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out float value)
            || !float.IsFinite(value))
        {
            throw InvalidValue();
        }

        return value;
    }

    private static ArgumentException InvalidValue() =>
        new("A Covenant operational default requires a finite bounded unsigned decimal scalar.", "content");
}

/// <summary>A reserved Confirmed artifact cannot satisfy the operational catalog contract.</summary>
internal sealed class CovenantOperationalDefaultInvalidException : Exception;
