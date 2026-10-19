using System.Collections.Immutable;

using System.Globalization;

using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

/// <summary>Versioned, domain-separated, length-framed SHA-256 identities using Covenant primitives.</summary>
public static class MemoryEvaluationDigests
{
    public static CovenantDigest CheckPolicy(MemoryEvaluationCheck check)
    {
        using CovenantCanonicalHashWriter writer = Writer("check-policy");
        writer.WriteUtf8(CheckIdentity(check));
        return writer.FinalizeDigest();
    }

    public static CovenantDigest SourceManifest(ImmutableArray<MemoryEvaluationSourceCoordinate> sources, ImmutableArray<MemoryEvaluationDependencyEdge> dependencies)
    {
        using CovenantCanonicalHashWriter writer = Writer("source-manifest");

        WriteSources(writer, sources);

        WriteDependencies(writer, dependencies);

        return writer.FinalizeDigest();
    }

    public static CovenantDigest Request(MemoryEvaluationValidatedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using CovenantCanonicalHashWriter writer = Writer("request");

        MemoryEvaluationRequest value = request.Request;

        writer.WriteGuid(value.EvaluationId);

        writer.WriteInt32((int)value.Scope.Kind);

        writer.WriteOptional(value.Scope.CampaignId, static (target, id) => target.WriteGuid(id));

        writer.WriteUtf8(value.ProviderName);

        writer.WriteUtf8(value.Model);

        WriteOptions(writer, value.Options);

        writer.WriteUtf8(value.Workload.SystemPrompt);

        writer.WriteUtf8(value.Workload.UserPrompt);

        writer.WriteInt32(value.PairCount);

        writer.WriteCount((ulong)value.Memory.Covenant.Length);

        foreach (MemoryEvaluationCovenantTarget target in value.Memory.Covenant.OrderBy(target => target.EntryId).ThenBy(target => target.VersionId))
        {
            writer.WriteGuid(target.EntryId);

            writer.WriteGuid(target.VersionId);

            writer.WriteFixed32(target.FragmentDigest.Span);
        }

        writer.WriteCount((ulong)value.Memory.Saga.Length);

        foreach (MemoryEvaluationSagaTarget target in value.Memory.Saga.OrderBy(target => target.MemoryId, StringComparer.Ordinal).ThenBy(target => target.VersionId, StringComparer.Ordinal))
        {
            writer.WriteUtf8(target.MemoryId);

            writer.WriteUtf8(target.VersionId);

            writer.WriteInt32((int)target.HashFormat);

            writer.WriteFixed32(target.ContentDigest.Span);
        }

        writer.WriteCount((ulong)value.Memory.Lexicon.Length);

        foreach (MemoryEvaluationLexiconTarget target in value.Memory.Lexicon.OrderBy(target => target.EntryId).ThenBy(target => target.VersionId, StringComparer.Ordinal))
        {
            writer.WriteGuid(target.EntryId);

            writer.WriteUtf8(target.VersionId);

            writer.WriteInt32((int)target.HashFormat);

            writer.WriteFixed32(target.ContentDigest.Span);
        }

        writer.WriteCount((ulong)value.Checks.Length);

        foreach (MemoryEvaluationCheck check in value.Checks)
        {
            WriteCheck(writer, check);
        }

        return writer.FinalizeDigest();
    }

    public static CovenantDigest ExecutionPlan(MemoryEvaluationExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        using CovenantCanonicalHashWriter writer = Writer("execution-plan");

        writer.WriteFixed32(Request(plan.Request).Span);

        writer.WriteInt32(plan.PolicyVersion);

        writer.WriteFixed32(plan.Sources.ManifestDigest.Span);

        WriteSources(writer, plan.Sources.Coordinates);

        writer.WriteUtf8(plan.Sources.RenderedMemory);

        writer.WriteUtf8(plan.Prompts.ControlSystemPrompt);

        writer.WriteUtf8(plan.Prompts.ControlUserPrompt);

        writer.WriteUtf8(plan.Prompts.TreatmentSystemPrompt);

        writer.WriteUtf8(plan.Prompts.TreatmentUserPrompt);

        writer.WriteFixed32(plan.ProviderDigest.Span);

        writer.WriteFixed32(plan.ModelDigest.Span);

        writer.WriteFixed32(plan.OptionsDigest.Span);

        writer.WriteFixed32(plan.DestinationDigest.Span);

        writer.WriteFixed32(plan.SensitivityDigest.Span);

        writer.WriteInt64(plan.ConfigurationGeneration);

        writer.WriteCount((ulong)plan.SendOrder.Length);

        foreach (MemoryEvaluationArm arm in plan.SendOrder)
        {
            writer.WriteInt32((int)arm);
        }

        WriteDependencies(writer, plan.Sources.Dependencies);

        writer.WriteOptionalReference(plan.Sources.Fence, static (target, fence) =>
        {
            target.WriteGuid(fence.DatasetGeneration);

            target.WriteInt64(fence.AdmissionGeneration);

            target.WriteInt64(fence.RuntimeAuthorityGeneration);

            target.WriteInt64(fence.CampaignAvailabilityGeneration);

            target.WriteFixed32(fence.SourceStateDigest.Span);
        });

        writer.WriteOptionalReference(plan.Sources.Provenance, static (target, provenance) =>
        {
            target.WriteInt32((int)provenance.Mode);

            target.WriteCount((ulong)provenance.ExactGenerationIds.Length);

            foreach (Guid id in provenance.ExactGenerationIds)
            {
                target.WriteGuid(id);
            }

            target.WriteBytes(provenance.BloomBits.AsSpan());
        });

        writer.WriteCount((ulong)plan.Sources.Fragments.Length);

        foreach (MemoryEvaluationSourceFragment fragment in plan.Sources.Fragments)
        {
            WriteSourceReference(writer, plan.Sources.Coordinates, fragment.SourceIndex);

            writer.WriteUtf8(fragment.Text);

            writer.WriteInt32((int)fragment.Placement);
        }

        writer.WriteCount((ulong)plan.Prompts.Occurrences.Length);

        foreach (MemoryEvaluationPromptOccurrence occurrence in plan.Prompts.Occurrences)
        {
            WriteSourceReference(writer, plan.Sources.Coordinates, occurrence.SourceIndex);

            writer.WriteInt32((int)occurrence.Placement);

            writer.WriteInt32(occurrence.StartUtf8);

            writer.WriteInt32(occurrence.LengthUtf8);

            writer.WriteFixed32(occurrence.ContentDigest.Span);
        }

        writer.WriteFixed32(plan.Prompts.UnchangedSpansDigest.Span);

        return writer.FinalizeDigest();
    }

    public static CovenantDigest Report(MemoryEvaluationTerminalReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        using CovenantCanonicalHashWriter writer = Writer("terminal-report");

        MemoryEvaluationRegistration registration = report.Registration;

        writer.WriteGuid(registration.EvaluationId);

        writer.WriteGuid(registration.OperationId);

        WriteTime(writer, registration.CreatedAtUtc);

        writer.WriteGuid(registration.Ownership.DatasetGeneration);

        writer.WriteInt32((int)registration.Ownership.Scope);

        writer.WriteOptional(registration.Ownership.CampaignId, static (target, id) => target.WriteGuid(id));

        writer.WriteFixed32(registration.RequestDigest.Span);

        writer.WriteFixed32(registration.PlanDigest.Span);

        writer.WriteFixed32(registration.WorkloadDigest.Span);

        writer.WriteFixed32(registration.ProviderDigest.Span);

        writer.WriteFixed32(registration.ModelDigest.Span);

        writer.WriteFixed32(registration.OptionsDigest.Span);

        writer.WriteFixed32(registration.PromptDigest.Span);

        writer.WriteFixed32(registration.EvaluatorDigest.Span);

        writer.WriteFixed32(registration.ManifestDigest.Span);

        writer.WriteInt32(registration.PairCount);

        writer.WriteInt32(registration.CheckCount);

        writer.WriteInt32(registration.PolicyVersion);

        writer.WriteInt32(registration.FormatVersion);

        WriteDependencies(writer, registration.Dependencies);

        writer.WriteOptional(registration.SourceAsOfUtc, WriteTime);

        WriteTime(writer, report.CompletedAtUtc);

        writer.WriteInt32((int)report.State);

        writer.WriteInt32((int)report.Decision.Outcome);

        writer.WriteInt32((int)report.Decision.Reason);

        WriteAggregate(writer, report.Aggregate);

        WriteSources(writer, report.Sources);

        WriteDependencies(writer, report.Dependencies);

        writer.WriteOptional(report.InputTokens, static (target, count) => target.WriteInt64(count));

        writer.WriteOptional(report.OutputTokens, static (target, count) => target.WriteInt64(count));

        writer.WriteOptional(report.Cost, static (target, cost) => target.WriteUtf8(cost.ToString("G29", CultureInfo.InvariantCulture)));

        writer.WriteInt64(report.ElapsedMilliseconds);

        return writer.FinalizeDigest();
    }

    internal static string CheckIdentity(MemoryEvaluationCheck check)
    {
        using CovenantCanonicalHashWriter writer = Writer("check");

        WriteCheck(writer, check);

        return writer.FinalizeDigest().ToString();
    }

    private static CovenantCanonicalHashWriter Writer(string purpose)
    {
        CovenantCanonicalHashWriter writer = new();

        writer.WriteUtf8("arcanum.memory-evaluation." + purpose + ".v1");

        return writer;
    }

    private static void WriteOptions(CovenantCanonicalHashWriter writer, MemoryEvaluationOptions options)
    {
        writer.WriteOptional(options.Temperature, static (target, value) => target.WriteBinary64(value));

        writer.WriteOptional(options.TopP, static (target, value) => target.WriteBinary64(value));

        writer.WriteOptional(options.Seed, static (target, value) => target.WriteInt64(value));

        writer.WriteInt32(options.MaxOutputTokens);

        writer.WriteOptional(options.ReasoningEffort, static (target, value) => target.WriteInt32((int)value));
    }

    private static void WriteCheck(CovenantCanonicalHashWriter writer, MemoryEvaluationCheck check)
    {
        switch (check)
        {
            case MemoryEvaluationRegexCheck regex:
                writer.WriteInt32((int)MemoryEvaluationCheckKind.Regex);

                writer.WriteUtf8(regex.Pattern);

                writer.WriteByte(regex.ShouldMatch ? (byte)1 : (byte)0);

                writer.WriteByte(regex.IgnoreCase ? (byte)1 : (byte)0);

                break;

            case MemoryEvaluationJsonSchemaCheck schema:
                writer.WriteInt32((int)MemoryEvaluationCheckKind.JsonSchema);

                using (MemoryStream stream = new())
                {
                    using (Utf8JsonWriter json = new(stream))
                    {
                        WriteCanonicalJson(json, schema.Schema);
                    }

                    writer.WriteBytes(stream.ToArray());
                }

                break;

            default:
                throw new ArgumentException("Unsupported deterministic check.", nameof(check));
        }
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (JsonProperty property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);

                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();

                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();

                break;

            default:
                value.WriteTo(writer);

                break;
        }
    }

    private static void WriteAggregate(CovenantCanonicalHashWriter writer, MemoryEvaluationAggregate aggregate)
    {
        writer.WriteInt32(aggregate.CompletedPairs);

        writer.WriteInt32(aggregate.ValidPairs);

        writer.WriteInt32(aggregate.InvalidPairs);

        writer.WriteInt32(aggregate.ImprovedPairs);

        writer.WriteInt32(aggregate.RegressedPairs);

        writer.WriteInt32(aggregate.EquivalentPairs);

        writer.WriteInt32(aggregate.MixedPairs);

        writer.WriteInt32((int)aggregate.FailureReason);

        writer.WriteFixed32(aggregate.ResponseEvidenceDigest.Span);

        writer.WriteCount((ulong)aggregate.Checks.Length);

        foreach (MemoryEvaluationCheckAggregate check in aggregate.Checks.OrderBy(check => check.CheckIndex))
        {
            writer.WriteInt32(check.CheckIndex);

            writer.WriteInt32((int)check.Kind);

            writer.WriteFixed32(check.CheckPolicyDigest.Span);

            writer.WriteInt32(check.FailFail);

            writer.WriteInt32(check.FailPass);

            writer.WriteInt32(check.PassFail);

            writer.WriteInt32(check.PassPass);
        }
    }

    private static void WriteSourceReference(CovenantCanonicalHashWriter writer, ImmutableArray<MemoryEvaluationSourceCoordinate> sources, int sourceIndex)
    {
        if (sourceIndex < 0 || sourceIndex >= sources.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIndex), "A prompt source reference must name an exact manifest coordinate.");
        }

        // Coordinates form a canonical set, while indices refer to their retained input order.
        // Bind the actual exact coordinate so reordering plus remapping preserves attribution.
        WriteSources(writer, [sources[sourceIndex]]);
    }

    private static void WriteSources(CovenantCanonicalHashWriter writer, ImmutableArray<MemoryEvaluationSourceCoordinate> sources)
    {
        writer.WriteCount((ulong)sources.Length);

        foreach (MemoryEvaluationSourceCoordinate source in sources.OrderBy(source => source.Store).ThenBy(source => source.SubjectId, StringComparer.Ordinal).ThenBy(source => source.VersionId, StringComparer.Ordinal).ThenBy(source => source.Role))
        {
            writer.WriteInt32((int)source.Role);

            writer.WriteInt32((int)source.Store);

            writer.WriteUtf8(source.SubjectId);

            writer.WriteOptionalReference(source.ClaimId, static (target, value) => target.WriteUtf8(value));

            writer.WriteUtf8(source.VersionId);

            writer.WriteInt32(source.HashFormat);

            writer.WriteFixed32(source.ContentDigest.Span);

            writer.WriteOptional(source.DependencyRelation, static (target, value) => target.WriteInt32((int)value));

            writer.WriteOptional(source.Origin, static (target, value) => target.WriteInt32((int)value));

            writer.WriteInt32((int)source.Scope);

            writer.WriteOptional(source.CampaignId, static (target, value) => target.WriteGuid(value));

            writer.WriteOptional(source.ValidFromUtc, WriteTime);

            writer.WriteOptional(source.ValidToUtc, WriteTime);

            writer.WriteOptional(source.RecordedAtUtc, WriteTime);

            writer.WriteOptional(source.RecordedUntilUtc, WriteTime);

            writer.WriteInt32((int)source.Lifecycle);

            writer.WriteInt32((int)source.Sensitivity);

            writer.WriteOptionalReference(source.CanonicalOwnerSessionId, static (target, value) => target.WriteUtf8(value));

            writer.WriteOptionalReference(source.SourceSessionId, static (target, value) => target.WriteUtf8(value));

            writer.WriteOptional(source.Generation, static (target, value) => target.WriteGuid(value));

            writer.WriteInt32(source.Revision);

            writer.WriteOptional(source.Lane, static (target, value) => target.WriteInt32((int)value));

            writer.WriteOptional(source.Placement, static (target, value) => target.WriteInt32((int)value));

            writer.WriteOptional(source.CovenantOrigin, static (target, value) => target.WriteInt32((int)value));
        }
    }

    private static void WriteDependencies(CovenantCanonicalHashWriter writer, ImmutableArray<MemoryEvaluationDependencyEdge> edges)
    {
        writer.WriteCount((ulong)(edges.IsDefault ? 0 : edges.Length));

        if (edges.IsDefault)
        {
            return;
        }

        foreach (MemoryEvaluationDependencyEdge edge in edges.OrderBy(edge => edge.Store).ThenBy(edge => edge.DependentVersionId, StringComparer.Ordinal).ThenBy(edge => edge.Ordinal).ThenBy(edge => edge.DependencyVersionId, StringComparer.Ordinal))
        {
            writer.WriteInt32((int)edge.Store);

            writer.WriteUtf8(edge.DependentVersionId);

            writer.WriteUtf8(edge.DependencyVersionId);

            writer.WriteInt32((int)edge.Relation);

            writer.WriteInt32(edge.Ordinal);
        }
    }

    private static void WriteTime(CovenantCanonicalHashWriter writer, DateTimeOffset value) => writer.WriteInt64(value.UtcTicks);
}
