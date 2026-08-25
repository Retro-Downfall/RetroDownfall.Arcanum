using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// The two Covenant mutation handlers.
/// </summary>
/// <remarks>
/// Both are registered unconditionally in the cached internal-tool superset and both fail closed on
/// their own, and only proposal is advertised in this build. Advertisement is filtered per turn from
/// the live feature gate, canonical availability, invocation context, and tool policy, but a stale
/// cached partition or a direct internal invocation would bypass that filter, so each handler
/// rechecks the same facts before it accepts its capability.
///
/// <para>Neither handler ever writes canonical state itself. Both stage into the turn's collector,
/// and what happens to that collector is decided entirely by how the turn ends: a turn that reaches
/// its completed assistant finalization seals the collector and publishes the batch inside the same
/// transaction as the answer, and every other ending — interrupted, refused, cancelled, or simply
/// not Covenant-derived — discards it. A proposal therefore reaches the Campaign's Proposed lane
/// exactly when the answer it accompanied did, and retirement reaches nothing at all: its capability
/// is never minted, so its handler refuses before it can stage (§10.13, §10.14).</para>
/// </remarks>
internal sealed partial class ArcanumInternalToolServer
{

    internal const string CovenantMutationStagedStatus = "staged";

    internal const string CovenantMutationFailedStatus = "failed";

    /// <summary>
    /// Whether retirement can actually be performed, rather than merely refused.
    /// </summary>
    /// <remarks>
    /// Constant <see langword="false"/> in this build, and advertised accordingly. Retirement's
    /// capability requires the preflight disclosure a Ward showed the operator and the receipt proving
    /// they approved it; neither exists yet, so every call would be refused. Advertising a tool that
    /// always refuses teaches a model the capability is broken rather than absent, and an unbuilt
    /// capability must not look like a built one that happens to be failing.
    ///
    /// <para>The handler stays registered regardless, so a direct or stale invocation still fails
    /// closed rather than reaching an unregistered name.</para>
    /// </remarks>
    private static bool CovenantRetirementAvailable => false;

    private bool CovenantToolsAvailable()
    {

        if (_covenantCapabilities is null || _covenantAvailability is null)
        {
            return false;
        }

        CovenantAvailabilitySnapshot snapshot = _covenantAvailability.Current;

        return snapshot.FeatureEnabled && snapshot.Canonical == CovenantCapabilityState.Healthy;

    }

    private async Task<McpToolsCallResultWire> ExecuteProposeCovenantAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {

        if (!TryBeginCovenantCall(
                CovenantToolNames.ProposeCovenant,
                arguments,
                out CovenantToolCapabilityGrant? grant,
                out CovenantDigest toolInputDigest,
                out McpToolsCallResultWire? refusal))
        {
            return refusal!;
        }

        ProposeCovenantParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.ProposeCovenantParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "propose_covenant argument deserialization failed.");

            return CovenantFailure(ErrorCodes.Covenant.InvalidScope, "Invalid arguments for propose_covenant.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Key) || string.IsNullOrWhiteSpace(args.Content))
        {
            return CovenantFailure(
                ErrorCodes.Covenant.InvalidScope,
                "propose_covenant requires a non-empty 'key' and 'content'.");
        }

        CovenantToolInvocationContext capability = grant!.Capability;

        Result<IDisposable> lease = capability.TryAcquireUse(grant.Nonce);

        if (lease.IsFailure)
        {
            return CovenantFailure(lease.Error);
        }

        using IDisposable use = lease.Value;

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            ICovenantCompiler compiler = scope.ServiceProvider.GetRequiredService<ICovenantCompiler>();

            CovenantCompiledContent compiled;

            try
            {
                compiled = compiler.Compile(args.Key, args.Content);
            }
            catch (ArgumentException ex)
            {
                // The compiler's rejection reason is about the operator's own text, so the model gets
                // the typed code and a fixed sentence rather than an echo of what it just sent.
                _logger?.LogInformation(
                    "propose_covenant rejected content that failed compilation: {Reason}.",
                    ex.GetType().Name);

                return CovenantFailure(
                    ErrorCodes.Covenant.InvalidScope,
                    "propose_covenant could not compile that key and content under the Covenant text policy.");
            }

            Result<CovenantProposedLaneExpectation> expectation = await ResolveProposedLaneAsync(
                    capability,
                    grant.Nonce,
                    compiled.NormalizedKey,
                    cancellationToken)
                .ConfigureAwait(false);

            if (expectation.IsFailure)
            {
                return CovenantFailure(expectation.Error);
            }

            Result<CovenantMutationIntent> intent = CovenantAgentMutationFactory.Propose(
                capability,
                compiled,
                expectation.Value.Revision,
                expectation.Value.KeyEpoch,
                toolInputDigest);

            return intent.IsFailure
                ? CovenantFailure(intent.Error)
                : StageCovenantMutation(capability, grant.Nonce, intent.Value, toolInputDigest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "propose_covenant failed.");

            return CovenantFailure(ErrorCodes.Covenant.MaintenanceFailed, "An internal error occurred during tool execution.");
        }

    }

    private Task<McpToolsCallResultWire> ExecuteRetireCovenantAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryBeginCovenantCall(
                CovenantToolNames.RetireCovenant,
                arguments,
                out CovenantToolCapabilityGrant? grant,
                out CovenantDigest toolInputDigest,
                out McpToolsCallResultWire? refusal))
        {
            return Task.FromResult(refusal!);
        }

        RetireCovenantParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.RetireCovenantParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "retire_covenant argument deserialization failed.");

            return Task.FromResult(CovenantFailure(
                ErrorCodes.Covenant.InvalidScope,
                "Invalid arguments for retire_covenant."));
        }

        if (args is null
            || string.IsNullOrWhiteSpace(args.Key)
            || !Enum.TryParse(args.Lane, ignoreCase: false, out CovenantLane lane)
            || lane is not (CovenantLane.Confirmed or CovenantLane.Proposed))
        {
            return Task.FromResult(CovenantFailure(
                ErrorCodes.Covenant.InvalidScope,
                "retire_covenant requires a non-empty 'key' and a 'lane' of Confirmed or Proposed."));
        }

        CovenantToolInvocationContext capability = grant!.Capability;

        if (capability.RetirementPreflight is not { } preflight)
        {
            return Task.FromResult(CovenantFailure(
                ErrorCodes.Covenant.IneligibleTurn,
                "This turn resolved no retirement target for that key."));
        }

        CovenantKey requestedKey;

        try
        {
            requestedKey = new CovenantKey(args.Key.Trim());
        }
        catch (ArgumentException)
        {
            return Task.FromResult(CovenantFailure(
                ErrorCodes.Covenant.InvalidScope,
                "retire_covenant requires a well-formed Covenant key."));
        }

        // The Ward was shown one exact target. A call that now names a different key or lane is not
        // the call the operator approved, whatever the model intended.
        if (!string.Equals(preflight.NormalizedKey, requestedKey.Value, StringComparison.Ordinal)
            || preflight.Lane != lane)
        {
            return Task.FromResult(CovenantFailure(
                ErrorCodes.Covenant.StaleSnapshot,
                "This retirement does not name the target the operator was shown."));
        }

        Result<IDisposable> lease = capability.TryAcquireUse(grant.Nonce);

        if (lease.IsFailure)
        {
            return Task.FromResult(CovenantFailure(lease.Error));
        }

        using IDisposable use = lease.Value;

        Result<CovenantMutationIntent> intent = CovenantAgentMutationFactory.Retire(
            capability,
            toolInputDigest);

        return Task.FromResult(intent.IsFailure
            ? CovenantFailure(intent.Error)
            : StageCovenantMutation(capability, grant.Nonce, intent.Value, toolInputDigest));

    }

    /// <summary>
    /// Rechecks the live facts, resolves the capability for this request, and binds the call's
    /// evidence digest, or produces the exact refusal the model should see.
    /// </summary>
    private bool TryBeginCovenantCall(
        string toolName,
        JsonElement arguments,
        out CovenantToolCapabilityGrant? grant,
        out CovenantDigest toolInputDigest,
        out McpToolsCallResultWire? refusal)
    {

        grant = null;

        toolInputDigest = default;

        if (!CovenantToolsAvailable())
        {
            refusal = CovenantFailure(
                ErrorCodes.Covenant.Unavailable,
                "Covenant memory is not available on this installation right now.");

            return false;
        }

        if (CovenantToolInvocationAmbient.Current is not { } resolved)
        {
            refusal = CovenantFailure(
                ErrorCodes.Covenant.IneligibleTurn,
                "This turn carries no Covenant staging capability.");

            return false;
        }

        if (!string.Equals(resolved.Capability.ToolName, toolName, StringComparison.Ordinal))
        {
            refusal = CovenantFailure(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This Covenant capability authorizes a different tool.");

            return false;
        }

        Result<ProviderToolCallClassification> classified = CovenantToolClassifier.ClassifyCovenantTool(
            toolName,
            RawArgumentBytes(arguments));

        if (classified.IsFailure)
        {
            refusal = CovenantFailure(classified.Error);

            return false;
        }

        grant = resolved;

        toolInputDigest = classified.Value.ToolInputDigest;

        refusal = null;

        return true;

    }

    /// <summary>The lane revision and key epoch a staged proposal will compare and swap against.</summary>
    private readonly record struct CovenantProposedLaneExpectation(long Revision, long KeyEpoch);

    /// <summary>
    /// Resolves the Proposed lane's expectation: the turn plan first, then one bounded head probe.
    /// </summary>
    /// <remarks>
    /// Revision zero is only ever proved, never assumed. An absent head means the lane was never
    /// created; a tombstone means the operator retired it, and an agent reactivating that silently is
    /// exactly the "re-proposed every week" behavior the lifecycle exists to prevent.
    ///
    /// <para>A plan hit carries a revision but no key-reclamation epoch — the turn snapshot does not
    /// project one — so it expects the unreclaimed epoch zero. That is a conservative assumption, not
    /// a silent one: publication reads the real epoch inside its write transaction and refuses the
    /// batch with <c>Covenant.StaleSnapshot</c> if it disagrees. Only a key that was reclaimed and
    /// then re-materialized into the same turn plan can hit it, and it fails closed.</para>
    /// </remarks>
    private static async ValueTask<Result<CovenantProposedLaneExpectation>> ResolveProposedLaneAsync(
        CovenantToolInvocationContext capability,
        CovenantToolCapabilityNonce nonce,
        string normalizedKey,
        CancellationToken cancellationToken)
    {

        foreach (CovenantPlanCandidateDecision decision in capability.ProducingAdmission.Plan.Decisions)
        {

            if (decision.Candidate.Scope == CovenantScope.Campaign
                && decision.Candidate.Lane == CovenantLane.Proposed
                && string.Equals(decision.Candidate.NormalizedKey.Value, normalizedKey, StringComparison.Ordinal))
            {
                return Result<CovenantProposedLaneExpectation>.Success(
                    new CovenantProposedLaneExpectation(
                        checked((long)decision.Candidate.Revision),
                        KeyEpoch: 0));
            }

        }

        Result<CovenantLaneHeadProbe> probe = await capability
            .ProbeLaneHeadAsync(nonce, CovenantLane.Proposed, normalizedKey, cancellationToken)
            .ConfigureAwait(false);

        if (probe.IsFailure)
        {
            return Result<CovenantProposedLaneExpectation>.Failure(probe.Error);
        }

        return probe.Value.Presence switch
        {
            CovenantLaneHeadPresence.Absent => Result<CovenantProposedLaneExpectation>.Success(
                new CovenantProposedLaneExpectation(0, probe.Value.KeyEpoch)),
            CovenantLaneHeadPresence.Present => Result<CovenantProposedLaneExpectation>.Success(
                new CovenantProposedLaneExpectation(probe.Value.LaneRevision, probe.Value.KeyEpoch)),
            _ => Result<CovenantProposedLaneExpectation>.Failure(new Error(
                ErrorCodes.Covenant.LifecycleConflict,
                "That key was retired. Ask the operator to reinstate it rather than proposing it again.")),
        };

    }

    private McpToolsCallResultWire StageCovenantMutation(
        CovenantToolInvocationContext capability,
        CovenantToolCapabilityNonce nonce,
        CovenantMutationIntent intent,
        CovenantDigest toolInputDigest)
    {

        // Last recheck. Everything above this line is pure, and everything below it is visible to the
        // turn that publishes.
        Result live = capability.RecheckBeforeIrreversibleEffect(nonce);

        if (live.IsFailure)
        {
            return CovenantFailure(live.Error);
        }

        Result<ICovenantMutationCollector> collector = capability.ResolveCollector(nonce);

        if (collector.IsFailure)
        {
            return CovenantFailure(collector.Error);
        }

        Result<CovenantStagedMutationReceipt> staged = collector.Value.Stage(
            intent,
            capability.ProducingAdmission,
            toolInputDigest);

        if (staged.IsFailure)
        {
            return CovenantFailure(staged.Error);
        }

        return CovenantStaged(staged.Value, intent);

    }

    private McpToolsCallResultWire CovenantStaged(
        CovenantStagedMutationReceipt receipt,
        CovenantMutationIntent intent)
    {

        CovenantMutationStagedResultWire wire = new(
            CovenantMutationStagedStatus,
            receipt.MutationId.ToString("D"),
            receipt.OpaqueTargetDigest.ToString(),
            receipt.ScopeKind.ToString(),
            receipt.Lane.ToString(),
            intent.Operation.ToString(),
            receipt.ExpectedLaneRevision,
            receipt.RenderedHash?.ToString(),
            intent.Artifact?.CompiledByteCost);

        // The receipt says what actually happens, and what has not happened yet. A proposal becomes
        // durable with this turn's answer and not a moment sooner, so a model told plainly that the
        // write is still pending cannot report a stored preference for a turn that never finished.
        string text = intent.Operation == CovenantOperation.Set
            ? "Proposal staged. It is stored for the operator's review when this turn's reply is saved, and dropped with the turn if the reply never is. It waits in the Proposed lane and does not take effect until they confirm it, so describe it to them as suggested rather than as applied."
            : "Retirement staged for this turn only. This build has no path that applies it, so the standing preference is unchanged when the turn ends.";

        return CovenantResult(text, wire, _json.CovenantMutationStagedResultWire, isError: false);

    }

    private McpToolsCallResultWire CovenantFailure(Error error) =>
        CovenantFailure(error.Code, error.Message);

    private McpToolsCallResultWire CovenantFailure(string code, string message)
    {

        CovenantMutationFailureResultWire wire = new(CovenantMutationFailedStatus, code, message);

        return CovenantResult(message, wire, _json.CovenantMutationFailureResultWire, isError: true);

    }

    private McpToolsCallResultWire CovenantResult<T>(
        string text,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        bool isError) =>
        new()
        {
            Content =
            [
                new McpToolContentTextWire { Text = text },
            ],
            IsError = isError,
            StructuredContent = JsonSerializer.SerializeToElement(value, typeInfo),
        };

    private static byte[] RawArgumentBytes(JsonElement arguments) =>
        arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? []
            : Encoding.UTF8.GetBytes(arguments.GetRawText());

}
