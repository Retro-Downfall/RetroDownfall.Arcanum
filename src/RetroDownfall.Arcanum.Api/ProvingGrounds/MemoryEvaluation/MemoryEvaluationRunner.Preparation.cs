using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;

internal sealed partial class MemoryEvaluationRunner
{
    private MemoryEvaluationRegistration Registration(MemoryEvaluationExecutionPlan plan, Guid operationId, CovenantDigest requestDigest, CovenantDigest planDigest)
    {
        var request = plan.Request.Request;

        return new(request.EvaluationId, operationId, clock.GetUtcNow(), new(plan.Sources.Fence!.DatasetGeneration, request.Scope.Kind, request.Scope.CampaignId),
            requestDigest, planDigest, TextDigest("workload", request.Workload.SystemPrompt, request.Workload.UserPrompt), plan.ProviderDigest, plan.ModelDigest, plan.OptionsDigest,
            TextDigest("prompts", plan.Prompts.ControlSystemPrompt, plan.Prompts.ControlUserPrompt, plan.Prompts.TreatmentSystemPrompt, plan.Prompts.TreatmentUserPrompt),
            Digests("evaluator", request.Checks.Select(MemoryEvaluationDigests.CheckPolicy)), plan.Sources.ManifestDigest, request.PairCount, request.Checks.Length,
            Dependencies: plan.Sources.Dependencies, SourceAsOfUtc: plan.Sources.AsOfUtc);
    }

    private static CovenantDigest TextDigest(string purpose, params string[] values)
    {
        using CovenantCanonicalHashWriter writer = new();
        writer.WriteUtf8("arcanum.memory-evaluation." + purpose + ".v1");

        writer.WriteCount((ulong)values.Length);

        foreach (string value in values) writer.WriteUtf8(value);

        return writer.FinalizeDigest();
    }

    private static CovenantDigest Digests(string purpose, IEnumerable<CovenantDigest> values)
    {
        CovenantDigest[] all = values.ToArray();

        using CovenantCanonicalHashWriter writer = new();
        writer.WriteUtf8("arcanum.memory-evaluation." + purpose + ".v1");

        writer.WriteCount((ulong)all.Length);

        foreach (var value in all) writer.WriteFixed32(value.Bytes);

        return writer.FinalizeDigest();
    }

    /// <summary>Only bounded verdict vectors, response digests and measured counters survive an arm.</summary>
    private sealed class Progress(ImmutableArray<MemoryEvaluationCheck> checks, CovenantDigest planDigest)
    {
        private readonly MemoryEvaluationCheckAggregate[] _checks = checks.Select((check, index) => new MemoryEvaluationCheckAggregate(index,
            check is MemoryEvaluationRegexCheck ? MemoryEvaluationCheckKind.Regex : MemoryEvaluationCheckKind.JsonSchema,
            MemoryEvaluationDigests.CheckPolicy(check), 0, 0, 0, 0)).ToArray();

        private readonly List<CovenantDigest> _responses = [planDigest];

        private int _improved, _regressed, _equivalent, _mixed;

        internal int PhysicalSends { get; private set; }
        internal long InputTokens { get; private set; }
        internal long OutputTokens { get; private set; }
        internal decimal Cost { get; private set; }

        internal void AddSend(MemoryEvaluationArmResult result)
        {
            PhysicalSends++;

            _responses.Add(result.ResponseDigest);

            if (result.Usage.InputTokens is long input && result.Usage.OutputTokens is long output && result.Usage.Cost is decimal cost)
            {
                InputTokens = checked(InputTokens + input);

                OutputTokens = checked(OutputTokens + output);

                Cost = checked(Cost + cost);
            }
        }

        internal void AddPair(MemoryEvaluationArmResult control, MemoryEvaluationArmResult treatment)
        {
            switch (MemoryEvaluationPolicy.Relate(control.Verdicts, treatment.Verdicts))
            {
                case MemoryEvaluationPairRelation.Improved: _improved++; break;

                case MemoryEvaluationPairRelation.Regressed: _regressed++; break;

                case MemoryEvaluationPairRelation.Equivalent: _equivalent++; break;

                case MemoryEvaluationPairRelation.Mixed: _mixed++; break;
            }

            for (int index = 0; index < _checks.Length; index++)
            {
                bool c = control.Verdicts[index] == MemoryEvaluationVerdict.Pass;

                bool t = treatment.Verdicts[index] == MemoryEvaluationVerdict.Pass;

                var check = _checks[index];

                _checks[index] = check with
                {
                    FailFail = check.FailFail + (!c && !t ? 1 : 0), FailPass = check.FailPass + (!c && t ? 1 : 0),
                    PassFail = check.PassFail + (c && !t ? 1 : 0), PassPass = check.PassPass + (c && t ? 1 : 0)
                };
            }
        }

        internal MemoryEvaluationAggregate Aggregate()
        {
            int completed = _improved + _regressed + _equivalent + _mixed;

            return new(completed, completed, 0, _improved, _regressed, _equivalent, _mixed, [.. _checks], Digests("responses", _responses));
        }
    }
}
