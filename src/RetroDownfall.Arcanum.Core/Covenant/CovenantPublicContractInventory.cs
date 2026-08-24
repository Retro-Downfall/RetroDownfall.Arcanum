using System.Collections.Immutable;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// Which serialization context owns one frozen shape.
/// </summary>
/// <remarks>
/// Ownership is exclusive on purpose. A durable checkpoint that could also be serialized by the wire
/// context would inherit a public contract's compatibility rules, and a public payload that could be
/// written into a checkpoint would let a wire change rewrite recovery state (§10.16).
/// </remarks>
public enum CovenantContractSurface : byte
{

    /// <summary>An authenticated HTTP request or response, owned by <c>ArcanumJsonContext</c>.</summary>
    OperatorApi = 1,

    /// <summary>A CLI-only structured payload, owned by <c>CliJsonContext</c>.</summary>
    Cli = 2,

    /// <summary>A durable recovery payload, owned by an Infrastructure context.</summary>
    RecoveryCheckpoint = 3,

}

/// <summary>Which half of an exchange one shape is.</summary>
public enum CovenantContractDirection : byte
{

    Request = 1,

    Response = 2,

    Checkpoint = 3,

}

/// <summary>
/// One frozen public shape.
/// </summary>
/// <param name="WireTypeName">
/// The full name of the type. Resolved by the architecture suite, so an entry for a renamed or
/// deleted type fails rather than sitting here describing a contract nothing honours.
/// </param>
/// <param name="OwningPortTypeName">
/// The service port this shape crosses. Empty for a checkpoint, which crosses no port.
/// </param>
public sealed record CovenantPublicContract(
    string WireTypeName,
    CovenantContractSurface Surface,
    CovenantContractDirection Direction,
    string OwningPortTypeName,
    string Rationale);

/// <summary>One declared service port and the surface it serves.</summary>
public sealed record CovenantServicePort(string PortTypeName, string Rationale);

/// <summary>
/// One Covenant type that shares the wire naming convention and is deliberately not a wire shape.
/// </summary>
/// <remarks>
/// Declared rather than filtered out inside a test. The interesting failure is a genuine new payload
/// that nobody froze, and a discovery rule with a quiet exception list in the test would let the next
/// one hide behind the same exception. Here, excluding something is an edit to the inventory with a
/// reason attached, and the architecture suite proves the exclusion is honest by checking that the
/// type is not registered with the public wire context.
/// </remarks>
public sealed record CovenantNonWireShape(string TypeName, string Rationale);

/// <summary>
/// The closed inventory of every public Covenant wire shape, recovery checkpoint, and service port.
/// </summary>
/// <remarks>
/// Closed and enforced in both directions. Every declared entry must name a type that still exists,
/// and every public request or response type in this namespace must be declared. The failure this
/// exists to make loud is the second DTO for something that already has one: a duplicate compiles,
/// ships, and then diverges, and the divergence is discovered by a client rather than by a build.
///
/// <para>The shapes are declared before any endpoint serves them. The inventory describes the
/// contract each surface is held to, and an undeclared shape is a problem whether or not it is routed
/// yet — which is the entire point of freezing a contract ahead of the surfaces that consume it.</para>
/// </remarks>
public static class CovenantPublicContractInventory
{

    private const string Namespace = "RetroDownfall.Arcanum.Core.Covenant.";

    private const string InfrastructureNamespace = "RetroDownfall.Arcanum.Infrastructure.Data.Covenant.";

    private const string Management = Namespace + nameof(ICovenantManagementService);

    private const string Mutation = Namespace + nameof(ICovenantMutationService);

    private const string Maintenance = Namespace + nameof(ICovenantMaintenanceService);

    private const string CampaignPath = Namespace + nameof(ICampaignPathIdentityService);

    private const string SessionBinding = Namespace + nameof(ISessionCampaignBindingService);

    /// <summary>Every service port a public Covenant shape crosses.</summary>
    public static ImmutableArray<CovenantServicePort> Ports { get; } =
    [
        new(Management,
            "One authenticated read port for list, query, detail, versions, sources, explain, and status, "
            + "so every management route shares one lease, snapshot, and authority policy."),

        new(Mutation,
            "One port for prepare and commit, because the shared preflight calculator is what makes the "
            + "token binding meaningful."),

        new(Maintenance,
            "One port for schema repair, index rebuild, and family reinitialize, each of which closes "
            + "admission through the same exclusive gate."),

        new(CampaignPath,
            "One port for Campaign physical-root status, prepare, and apply, all executed by the host "
            + "that owns the filesystem rather than the client that asked."),

        new(SessionBinding,
            "One port for the single immutable resolution a legacy Session is allowed."),
    ];

    /// <summary>
    /// Covenant types whose names look like wire shapes and are not.
    /// </summary>
    public static ImmutableArray<CovenantNonWireShape> NonWireShapes { get; } =
    [
        new(Namespace + nameof(SessionTurnCapacityReservationRequest),
            "An in-process quota-guard argument. It names a Session and a reservation, is never "
            + "deserialized from a caller, and carrying it on the wire would let a client reserve "
            + "installation capacity directly."),

        new(Namespace + nameof(SessionTurnCapacityReleaseRequest),
            "The release half of the same in-process quota argument."),

        new(Namespace + nameof(DirectFinalizationCapacityRequest),
            "An in-process argument for the finalization path that has no claim to release."),

        new(Namespace + nameof(CovenantScopeCensus),
            "A store-port projection of content-free counts and byte totals. The operator sees it only "
            + "after the management port copies it into CovenantStatusDto, which is the declared wire "
            + "shape; this one crosses no boundary and carries no key, content, or Campaign identity."),

        new(Namespace + nameof(CovenantScopeCensusRow),
            "One bucket of that same in-process projection."),
    ];

    /// <summary>Every frozen wire shape and durable checkpoint.</summary>
    public static ImmutableArray<CovenantPublicContract> Contracts { get; } =
    [
        // Management reads.
        new(Namespace + nameof(CovenantListRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, Management,
            "A typed body, so Campaign identities, keys, filters, and cursors never enter a URL or an access log."),

        new(Namespace + nameof(CovenantQueryRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, Management,
            "Free-text inspection with the query in the body and bounded at 512 UTF-8 bytes and 32 terms."),

        new(Namespace + nameof(CovenantDetailRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, Management,
            "An exact scoped-key lookup; ambiguous all-scope detail is unrepresentable rather than refused."),

        new(Namespace + nameof(CovenantVersionsRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, Management,
            "History is paginated separately so one entry's edit count cannot unbound a list page."),

        new(Namespace + nameof(CovenantSourcesRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, Management,
            "At most 64 sources per version, so this read has no cursor and no truncation state."),

        new(Namespace + nameof(CovenantExplainRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, Management,
            "One explicit Global-only or Campaign evaluation, never an implicit one."),

        new(Namespace + nameof(CovenantHeadDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Management,
            "One current lane head with local lifecycle and effective eligibility reported separately."),

        new(Namespace + nameof(CovenantPageDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Management,
            "A bounded page carrying its own filter identity, search health, and truncation reason."),

        new(Namespace + nameof(CovenantSearchHealthDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Management,
            "Accelerator degradation is a field on a successful response, never an error."),

        new(Namespace + nameof(CovenantVersionDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Management,
            "One immutable version, including the compiler and renderer policy versions that produced it."),

        new(Namespace + nameof(CovenantVersionPageDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Management,
            "Descending by revision and version identity, with its own cursor."),

        new(Namespace + nameof(CovenantSourceDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Management,
            "One exact attachment-provenance leaf."),

        new(Namespace + nameof(CovenantSourcesDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Management,
            "Complete by construction, with the recomputed digest returned beside the stored one."),

        new(Namespace + nameof(CovenantDetailDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Management,
            "Both lane heads plus the key epoch a later mutation has to match."),

        new(Namespace + nameof(CovenantExplainSectionDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Management,
            "Rendered content only under the existing showContent gate plus clean read authority."),

        new(Namespace + nameof(CovenantExplainDecisionDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Management,
            "Why one candidate was or was not rendered, without disclosing what it says."),

        new(Namespace + nameof(CovenantExplainDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Management,
            "A fresh diagnostic snapshot and plan that publishes no mutation and never warms the turn cache."),

        new(Namespace + nameof(CovenantScopeCountDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Management,
            "One aggregate count for the shared memory-status surface."),

        new(Namespace + nameof(CovenantStatusDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Management,
            "Content-free capability health for a surface that is reachable wherever memory status is."),

        // Operator mutation.
        new(Namespace + nameof(CovenantSetPrepareRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, Mutation,
            "Carries every canonical field the commit will send, so preflight produces the exact request digest."),

        new(Namespace + nameof(CovenantRetirePrepareRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, Mutation,
            "Names the exact lane and revision being retired before anything is staged."),

        new(Namespace + nameof(CovenantSetRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, Mutation,
            "Repeats the prepared fields so the server re-derives the digest from what was actually sent."),

        new(Namespace + nameof(CovenantRetireRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, Mutation,
            "Optimistic concurrency plus the bound preflight token, with the mutation id as the sole replay key."),

        new(Namespace + nameof(CovenantMutationEffectExampleDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Mutation,
            "One affected Campaign inside a bounded example set."),

        new(Namespace + nameof(CovenantMutationEffectDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Mutation,
            "Exact affected count with truncated examples, never the other way round."),

        new(Namespace + nameof(CovenantMutationPreflightDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Mutation,
            "The server-authoritative plan and the token that binds it to the state it was computed from."),

        new(Namespace + nameof(CovenantMutationResultDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Mutation,
            "A NoChange outcome is reported exactly like an applied one, with replay stated explicitly."),

        // Maintenance and recovery.
        new(Namespace + nameof(CovenantSchemaRepairRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, Maintenance,
            "One of three safe repairs; anything else is manual recovery rather than a validation failure."),

        new(Namespace + nameof(CovenantIndexRebuildRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, Maintenance,
            "A caller-generated operation identity, so a retried rebuild resolves to the one it already started."),

        new(Namespace + nameof(CovenantFamilyReinitializePrepareRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, Maintenance,
            "Planning is a separate authenticated step because the operation it plans is irreversible."),

        new(Namespace + nameof(CovenantFamilyReinitializeApplyRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, Maintenance,
            "Operation identity, stable apply-request digest, and token, in the order receipt-first replay reads them."),

        new(Namespace + nameof(CovenantCatalogDefectDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Maintenance,
            "A named defect and a code, never a fragment of the catalog it describes."),

        new(Namespace + nameof(CovenantPreservedFamilyDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Maintenance,
            "What survives, so the confirmation states both halves of the trade."),

        new(Namespace + nameof(CovenantFamilyReinitializePlanDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Maintenance,
            "Every loss and preserved count with no Covenant content, because this is the largest confirmation the feature has."),

        new(Namespace + nameof(CovenantSchemaRepairResultDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, Maintenance,
            "The disposition, the phase the journal reached, and the resulting availability."),

        // Campaign path identity.
        new(Namespace + nameof(CampaignPathIdentityStatusRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, CampaignPath,
            "An explicit all-Campaign or single-Campaign selector, so a bulk report is always deliberate."),

        new(Namespace + nameof(CampaignPathPrepareRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, CampaignPath,
            "A closed operation set, with the path required exactly where one is opened."),

        new(Namespace + nameof(CampaignPathApplyRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, CampaignPath,
            "Replay is resolved from the durable operation before the token is examined."),

        new(Namespace + nameof(CampaignPathIdentityStatusItemDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, CampaignPath,
            "One Campaign's state plus the exact verb that resolves it."),

        new(Namespace + nameof(CampaignPathIdentityStatusPageDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, CampaignPath,
            "The bulk legacy-upgrade inventory, keyset-paginated like every other Covenant page."),

        new(Namespace + nameof(CampaignPathIdentityPlanDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, CampaignPath,
            "Facts computed by opening the target, which apply reopens and revalidates before committing."),

        new(Namespace + nameof(CampaignPathIdentityResultDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, CampaignPath,
            "The durable outcome, including how many turns were drained to reach it."),

        // Session binding.
        new(Namespace + nameof(SessionCampaignBindingStatusRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, SessionBinding,
            "An explicit all-Session or single-Session selector."),

        new(Namespace + nameof(SessionCampaignBindingPrepareRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, SessionBinding,
            "Names the scope the Session is moving to; the unresolved state is not a target."),

        new(Namespace + nameof(SessionCampaignBindingApplyRequest), CovenantContractSurface.OperatorApi, CovenantContractDirection.Request, SessionBinding,
            "The same receipt-first replay contract every other apply request uses."),

        new(Namespace + nameof(SessionCampaignBindingStatusItemDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, SessionBinding,
            "One Session's binding and whether it is already final."),

        new(Namespace + nameof(SessionCampaignBindingStatusPageDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, SessionBinding,
            "A keyset-paginated page of binding statuses."),

        new(Namespace + nameof(SessionCampaignBindingPlanDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, SessionBinding,
            "The prior binding-row digest and effect digest the token binds."),

        new(Namespace + nameof(SessionCampaignBindingResultDto), CovenantContractSurface.OperatorApi, CovenantContractDirection.Response, SessionBinding,
            "The one immutable choice, and whether this call is the one that made it."),

        // Durable recovery payloads. These cross no port and belong to Infrastructure.
        new(InfrastructureNamespace + "CovenantIndexRebuildCheckpointV1", CovenantContractSurface.RecoveryCheckpoint, CovenantContractDirection.Checkpoint, "",
            "A resumable rebuild's captured identities, so a mismatch discards derived state instead of publishing it."),

        new(InfrastructureNamespace + "CovenantFamilyReinitializeCheckpointV1", CovenantContractSurface.RecoveryCheckpoint, CovenantContractDirection.Checkpoint, "",
            "Closed phases and identity digests only: no path, key, content, handle, task, token, or service object."),

        new(InfrastructureNamespace + "DataRetentionMutationCheckpointV3", CovenantContractSurface.RecoveryCheckpoint, CovenantContractDirection.Checkpoint, "",
            "An interrupted retention mutation, plus the bounded optional Covenant arm that makes it an erasure."),

        new(InfrastructureNamespace + "CovenantResetEffectArmV1", CovenantContractSurface.RecoveryCheckpoint, CovenantContractDirection.Checkpoint, "",
            "The three fields recovery rebuilds a reset's exclusive owner from, and nothing else."),

        new(InfrastructureNamespace + "DataRetentionFactoryResetCheckpointV1", CovenantContractSurface.RecoveryCheckpoint, CovenantContractDirection.Checkpoint, "",
            "A healthy-catalog factory erasure's owner and the storage phase it reached."),
    ];

}
