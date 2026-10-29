using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.DataLifecycle;

/// <summary>
/// One inventoried class of Arcanum-owned persistent data.
/// </summary>
/// <remarks>
/// Every code is written literally because the value is persisted — retention policy rows and durable
/// operation checkpoints both store it — so reordering or removing a member repoints an existing row
/// at a different class. A rule an operator wrote for <see cref="SagaMemories"/> would silently begin
/// deleting something else, which is the one failure a retention enum must never have.
///
/// <para><see cref="Covenant"/> is inventoried and never swept. It has no
/// <see cref="Core.Configuration.RetentionSettings"/> property and
/// <see cref="DataRetentionSettingsCatalog.ResolveRule"/> resolves it to null, because its versions,
/// heads, provenance, tombstones, and disclosure receipts are the evidence that makes an erasure claim
/// checkable. A rule that could age them out would destroy the only record of what was destroyed
/// (§10.20.1).</para>
///
/// <para><see cref="MemoryErasureEvidence"/> is inventoried and never swept for the same kind of reason:
/// it has no rule, and status reports it as content-free counts.</para>
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<RetentionDataClass>))]
public enum RetentionDataClass
{
    ActiveSessions = 0,

    ArchivedSessions = 1,

    Entries = 2,

    AttachmentVersions = 3,

    AttachmentBytes = 4,

    AttachmentChunks = 5,

    AttachmentEmbeddings = 6,

    UploadedFiles = 7,

    BatchInputFiles = 8,

    BatchOutputFiles = 9,

    BatchErrorFiles = 10,

    CompletedBatches = 11,

    SagaMemories = 12,

    LexiconEntries = 13,

    WorkspaceChunks = 14,

    WorkspaceEmbeddings = 15,

    SessionEntryEmbeddings = 16,

    Tapestry = 17,

    AuditLogs = 18,

    GuardrailLogs = 19,

    IdempotencyClaims = 20,

    InferenceRuns = 21,

    BillableOperations = 22,

    BudgetReservations = 23,

    CostAdjustments = 24,

    LongRunningOperations = 25,

    SanctumBreaches = 26,

    DaemonExecutions = 27,

    /// <summary>The Covenant family. Inventoried, never aged out.</summary>
    Covenant = 28,

    /// <summary>
    /// The Annals. Inventoried, never aged out on its own timer: a claim's lifecycle is its subject's.
    /// </summary>
    Annals = 29,

    /// <summary>
    /// Erasure evidence: the content-free fingerprints, receipts, and receipt subjects selective
    /// erasure records. Inventoried, never aged out.
    /// </summary>
    /// <remarks>
    /// A fingerprint is what keeps an erased item from being learned again, so a rule that aged one out
    /// would quietly lift an erasure on a schedule. Rows are removed only by a release, an operator
    /// re-creation of the erased identity, <c>memory erasure reset-key</c>, restore's
    /// destination-authoritative join, or a full installation reset, which removes the Grimoire and the
    /// key together.
    /// </remarks>
    MemoryErasureEvidence = 30,

    /// <summary>Bounded Campaign continuity summaries and their contribution metadata.</summary>
    CampaignSummaries = 31,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<DataRetentionOperation>))]
public enum DataRetentionOperation
{
    Prune,

    DeleteSession,

    DeleteAttachment,

    ResetMemory,

    ResetWorkspace,

    FactoryReset,
}

/// <summary>
/// The one named store a memory reset clears. There is deliberately no generic "all memory" arm.
/// </summary>
/// <remarks>
/// Literal codes for the same reason <see cref="RetentionDataClass"/> has them: the value reaches a
/// durable operation checkpoint, and a renumbered member would let a resumed reset clear a store the
/// operator never selected.
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryResetScope>))]
public enum MemoryResetScope
{
    Entry = 0,

    Attachments = 1,

    Workspace = 2,

    Saga = 3,

    Lexicon = 4,

    /// <summary>
    /// The Covenant family. Inventoried and planned here; apply enters the exclusive erasure
    /// coordinator rather than the ordinary memory-reset executor (§10.20.1).
    /// </summary>
    Covenant = 5,

    /// <summary>Campaign continuity summaries, optionally narrowed to one owning Campaign.</summary>
    CampaignSummary = 6,
}

public sealed record DataRetentionRequest(
    [property: JsonRequired] DataRetentionOperation Operation = DataRetentionOperation.Prune,
    Guid? TargetId = null,
    MemoryResetScope? MemoryScope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DataRetentionWorkspaceBinding? Workspace = null);

public sealed record DataRetentionWorkspaceBinding(
    [property: JsonRequired] Guid CampaignId,
    [property: JsonRequired] string WorkspaceRoot);

public sealed record DataRetentionApplyRequest(
    [property: JsonRequired] DataRetentionRequest Request,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpectedPlanId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? RequestedOperationId = null);

public sealed record RetentionRuleUpdateRequest(
    [property: JsonRequired] string DataClass,
    [property: JsonRequired] bool Enabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Days = null);

/// <remarks>
/// <paramref name="CampaignId"/> narrows the reset to the memories one Campaign owns, leaving every
/// other Campaign's and every installation-scoped memory in place. The stores that carry an owning
/// Campaign - <see cref="MemoryResetScope.Saga"/>, <see cref="MemoryResetScope.Lexicon"/>, and
/// <see cref="MemoryResetScope.CampaignSummary"/> - accept it; naming it for any other store is refused
/// rather than silently widened to all of it.
/// </remarks>
public sealed record MemoryResetRequest(
    [property: JsonRequired] MemoryResetScope Scope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpectedPlanId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? CampaignId = null);

public sealed record FactoryResetRequest(
    [property: JsonRequired] string Confirmation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpectedPlanId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? RequestedOperationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    InstallationResetHostHandoff? InstallationResetHandoff = null);

/// <summary>
/// The content-free installation inventory of the Covenant family.
/// </summary>
/// <remarks>
/// Counts only. No subject text, key, digest preimage, path, or Campaign identity appears here,
/// because <c>data status</c> and a retention rehearsal are both readable by anything that can reach
/// the route and neither has any business carrying protected content (§10.20.1).
///
/// <para><paramref name="DisclosureCountKind"/> is carried rather than resolved away for the reason
/// §10.18 gives: a joined bucket is a lower bound, and reporting a lower bound as exact would
/// understate exposure in exactly the report an operator uses to decide. Only nonrevocable buckets
/// are folded — a locally revocable effect is one Arcanum can still undo, and including it would
/// inflate the number.</para>
/// </remarks>
public sealed record DataRetentionCovenantInventory(
    long Rows,
    long ManagedFiles,
    long LocalArtifacts,
    long AffectedSessions,
    long PossibleDisclosures,
    CovenantDisclosureCountKind DisclosureCountKind);

/// <summary>
/// The content-free inventory of erasure evidence: how many fingerprints, receipts, and receipt
/// subjects the installation holds.
/// </summary>
/// <remarks>
/// Counts only. No fingerprint, key identifier, digest, store, scope, or Campaign identity appears here.
/// The evidence is never selected by a prune or a reset, so the inventory is a report rather than a
/// candidate set and stays outside every plan identity: releasing one fingerprint does not expire a
/// factory preview that never proposed to remove it.
/// </remarks>
public sealed record DataRetentionMemoryErasureInventory(
    long Fingerprints,
    long Receipts,
    long ReceiptSubjects);

public sealed record DataRetentionStatusItem(
    RetentionDataClass DataClass,
    long Rows,
    long Files,
    long EstimatedBytes,
    bool PolicyEnabled,
    int? RetentionDays,
    string Store,
    string Provenance);

public sealed record DataRetentionStatus(
    DateTimeOffset GeneratedAt,
    DataRetentionStatusItem[] Items,
    long Rows,
    long Files,
    long EstimatedBytes,
    string[] PreservedOutsideSelectedRoot,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DataRetentionCovenantInventory? Covenant = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DataRetentionMemoryErasureInventory? MemoryErasure = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CovenantInventoryErrorCode = null);

public sealed record DataRetentionPlanItem(
    RetentionDataClass DataClass,
    long Rows,
    long Files,
    long EstimatedBytes,
    long DerivedRecords);

public sealed record DataRetentionBlocker(
    RetentionDataClass DataClass,
    string ResourceId,
    string ReasonCode,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? HoldingSessionId = null);

public sealed record DataRetentionConflict(
    string Code,
    string ResourceId,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? HoldingSessionId = null);

/// <summary>
/// What operator curation of the Saga store means for one plan: how many memories are pinned, and how
/// many of those this plan would otherwise have selected.
/// </summary>
/// <remarks>
/// <see cref="PinnedRowsExemptFromPlan"/> counts the pinned rows this plan's own cutoff would otherwise
/// have selected. Reporting it is what keeps a dry-run honest: a preview that silently omitted the
/// exempted rows would tell an operator their retention rule reaches further than it does.
///
/// <para>Retention is the whole of what a pin exempts a memory from today. There is no consolidation
/// sweep and no decay pass in the installation for it to bind, so this inventory has nothing else to
/// report. The state is durable, and the sweeps that arrive inherit it.</para>
/// </remarks>
public sealed record DataRetentionSagaCurationInventory(
    long PinnedRows,
    long PinnedRowsExemptFromPlan);

/// <summary>
/// Total pinned Lexicon entries and the pinned entries this plan's retention cutoff would otherwise select.
/// </summary>
public sealed record DataRetentionLexiconCurationInventory(
    long PinnedRows,
    long PinnedRowsExemptFromPlan);

public sealed record DataRetentionPlan(
    string PlanId,
    DataRetentionRequest Request,
    DateTimeOffset GeneratedAt,
    DataRetentionPlanItem[] Items,
    DataRetentionBlocker[] Blockers,
    DataRetentionConflict[] Conflicts,
    long Rows,
    long Files,
    long EstimatedBytes,
    long DerivedRecords,
    string[] CandidateIds,
    bool RequiresConfirmation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DataRetentionCovenantInventory? Covenant = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DataRetentionSagaCurationInventory? SagaCuration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DataRetentionLexiconCurationInventory? LexiconCuration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DataRetentionMemoryErasureInventory? MemoryErasure = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DataRetentionDiagnostics? Diagnostics = null);

/// <summary>
/// A plan and the optional Covenant read lease that protects its response until serialization ends.
/// </summary>
/// <remarks>
/// The lease is transferred to the HTTP result, which revalidates it immediately before writing and
/// disposes it after the final byte. Callers that only need a plan use <see cref="IDataRetentionService.PlanAsync"/>,
/// which preserves the legacy ownership boundary by disposing any lease before it returns.
/// </remarks>
public sealed record DataRetentionPlanAdmission(
    DataRetentionPlan Plan,
    ICovenantSnapshotReadLease? ReadLease);

/// <summary>
/// The Covenant capability a plan-admission caller needs held through its response.
/// </summary>
public enum DataRetentionPlanAdmissionCapability
{
    Request = 0,

    Installation = 1,
}

public sealed record DataRetentionApplyResult(
    Guid OperationId,
    string PlanId,
    long RowsDeleted,
    long FilesDeleted,
    long EstimatedBytesDeleted,
    long DerivedRecordsDeleted,
    bool Reconciled,
    DataRetentionBlocker[] Blockers,
    DataRetentionConflict[] Conflicts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? RequestedOperationId = null);

public interface IDataRetentionService
{
    Task<DataRetentionStatus> GetStatusAsync(
        CancellationToken cancellationToken = default);

    Task<DataRetentionPlan> PlanAsync(
        DataRetentionRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<DataRetentionPlanAdmission>> PlanAdmissionAsync(
        DataRetentionRequest request,
        CancellationToken cancellationToken = default,
        DataRetentionPlanAdmissionCapability capability = DataRetentionPlanAdmissionCapability.Request);

    Task<Result<DataRetentionDiagnosticPageAdmission>> ReadPruneDiagnosticsAdmissionAsync(
        DataRetentionPruneDiagnosticsRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<DataRetentionApplyResult>> ApplyAsync(
        DataRetentionApplyRequest request,
        CancellationToken cancellationToken = default);
}

public static class DataRetentionSettingsCatalog
{
    public static RetentionRuleSettings? ResolveRule(RetentionSettings settings, RetentionDataClass dataClass) =>
        GetRuleName(dataClass) switch
        {
            "active-sessions" => settings.ActiveSessions,

            "archived-sessions" => settings.ArchivedSessions,

            "entries" => settings.Entries,

            "attachments" => settings.Attachments,

            "uploaded-files" => settings.UploadedFiles,

            "completed-batches" => settings.CompletedBatches,

            "saga-memories" => settings.SagaMemories,

            "lexicon-entries" => settings.LexiconEntries,

            "campaign-summaries" => settings.CampaignSummaries,

            "workspace-indexes" => settings.WorkspaceIndexes,

            "session-entry-embeddings" => settings.SessionEntryEmbeddings,

            "audit-logs" => settings.AuditLogs,

            "guardrail-logs" => settings.GuardrailLogs,

            "idempotency-claims" => settings.IdempotencyClaims,

            "accounting" => settings.Accounting,

            "long-running-operations" => settings.LongRunningOperations,

            "sanctum-breaches" => settings.SanctumBreaches,

            "daemon-history" => settings.DaemonHistory,

            _ => null,
        };

    public static string? GetRuleName(RetentionDataClass dataClass) =>
        dataClass switch
        {
            RetentionDataClass.ActiveSessions => "active-sessions",

            RetentionDataClass.ArchivedSessions => "archived-sessions",

            RetentionDataClass.Entries => "entries",

            RetentionDataClass.AttachmentVersions
                or RetentionDataClass.AttachmentBytes
                or RetentionDataClass.AttachmentChunks
                or RetentionDataClass.AttachmentEmbeddings => "attachments",

            RetentionDataClass.UploadedFiles
                or RetentionDataClass.BatchInputFiles
                or RetentionDataClass.BatchOutputFiles
                or RetentionDataClass.BatchErrorFiles => "uploaded-files",

            RetentionDataClass.CompletedBatches => "completed-batches",

            RetentionDataClass.SagaMemories => "saga-memories",

            RetentionDataClass.LexiconEntries => "lexicon-entries",

            RetentionDataClass.CampaignSummaries => "campaign-summaries",

            RetentionDataClass.WorkspaceChunks
                or RetentionDataClass.WorkspaceEmbeddings => "workspace-indexes",

            RetentionDataClass.SessionEntryEmbeddings => "session-entry-embeddings",

            RetentionDataClass.AuditLogs => "audit-logs",

            RetentionDataClass.GuardrailLogs => "guardrail-logs",

            RetentionDataClass.IdempotencyClaims => "idempotency-claims",

            RetentionDataClass.InferenceRuns
                or RetentionDataClass.BillableOperations
                or RetentionDataClass.BudgetReservations
                or RetentionDataClass.CostAdjustments => "accounting",

            RetentionDataClass.LongRunningOperations => "long-running-operations",

            RetentionDataClass.SanctumBreaches => "sanctum-breaches",

            RetentionDataClass.DaemonExecutions => "daemon-history",

            // Explicit, not a fall-through. The Covenant family is inventoried and never aged out, and
            // an arm that says so cannot be mistaken for a rule somebody forgot to wire.
            RetentionDataClass.Covenant => null,

            // Explicit for the same reason. A claim's lifecycle is its subject's, and a rule that could
            // age one out from under a live memory would leave that memory unexplained.
            RetentionDataClass.Annals => null,

            // Explicit again. A fingerprint is what keeps an erased item from being learned again, so a
            // rule that aged one out would lift the erasure on a schedule nobody asked for.
            RetentionDataClass.MemoryErasureEvidence => null,

            _ => null,
        };

    /// <summary>Every class governed by the same rule, in the persisted class order.</summary>
    public static IReadOnlyList<RetentionDataClass> GetAffectedClasses(RetentionDataClass dataClass)
    {
        string? rule = GetRuleName(dataClass);

        return rule is null ? [] : Enum.GetValues<RetentionDataClass>()
            .Where(candidate => GetRuleName(candidate) == rule).ToArray();
    }

    /// <summary>Operator scope, including accounting history without its own class selector.</summary>
    public static string DescribeAffectedClasses(RetentionDataClass dataClass) =>
        string.Join(", ", GetAffectedClasses(dataClass).Select(DataRetentionDataClassParser.Format))
        + (GetRuleName(dataClass) == "accounting" ? " (including budget-alert history)" : string.Empty);

    /// <summary>Shared CLI and API validation, including the deliberately ruleless families.</summary>
    public static Result<RetentionDataClass> ParseConfigurableClass(string? selector)
    {
        string reason;

        if (!DataRetentionDataClassParser.TryParse(selector, out RetentionDataClass dataClass))
        {
            reason = $"Retention data class '{DataRetentionDataClassParser.DescribeInput(selector)}' is not recognized.";
        }
        else if (GetRuleName(dataClass) is not null)
        {
            return Result<RetentionDataClass>.Success(dataClass);
        }
        else
        {
            reason = dataClass switch
            {
                RetentionDataClass.Covenant => "The Covenant has no time-based retention rule and cannot be given one. It is inventoried by 'data status' and removed only by an explicit reset.",

                RetentionDataClass.MemoryErasureEvidence => "Erasure evidence has no time-based retention rule and cannot be given one. It is inventoried by 'data status' and removed only by a release, an operator re-creation of the erased item, 'memory erasure reset-key', a restore's destination-authoritative join, or a full installation reset.",

                RetentionDataClass.Annals => "The Annals have no time-based retention rule. An Annals claim follows its subject's lifecycle.",

                _ => "The retention data class has no configurable rule.",
            };

            reason += $" Selector: '{DataRetentionDataClassParser.DescribeInput(selector)}'.";
        }

        string choices = string.Join(", ", Enum.GetValues<RetentionDataClass>()
            .Where(candidate => GetRuleName(candidate) is not null)
            .Select(DataRetentionDataClassParser.Format)
            .Concat(Enum.GetValues<RetentionDataClass>().Select(GetRuleName).OfType<string>())
            .Distinct(StringComparer.Ordinal));

        return Result<RetentionDataClass>.Failure(new Error(ErrorCodes.Data.InvalidRequest,
            $"{reason} Configurable classes: {choices}."));
    }
}

/// <summary>The closed managed root whose factory inventory was refused.</summary>
public enum ManagedDataRootRole
{
    Attachments,

    Files,

    Logs,
}

/// <summary>A safe reason factory inventory could not establish its existing filesystem guards.</summary>
public enum ManagedTreeRefusalReason
{
    RootNotOrdinary,

    RootInspectionFailed,

    EntryIdentityUnavailable,

    EntryReparsePoint,

    UnsupportedEntry,

    FileIdentityChanged,

    FileInspectionFailed,

    DirectoryIdentityUnavailable,

    DirectoryEnumerationFailed,

    EntryInspectionFailed,
}

/// <summary>A managed-tree inventory refusal, distinct from storage or mutation uncertainty.</summary>
/// <remarks>
/// The exception message and public properties are closed diagnostic metadata only. The bounded,
/// escaped relative entry is retained privately for the requesting operator's error. Absolute roots,
/// external paths, symlink targets and original exceptions are never retained by this exception.
/// It says inventory was refused; only the caller's effect boundary can say whether nothing changed.
/// </remarks>
public sealed class ManagedTreeInventoryRefusalException : IOException
{
    private readonly string _entryDisplay;

    private ManagedTreeInventoryRefusalException(ManagedDataRootRole role, ManagedTreeRefusalReason reason, string entryDisplay)
        : base(ErrorCodes.Data.ManagedTreeRefused + ": root " + RoleName(role) + "; cause " + reason + ".")
    {
        RootRole = role;

        Reason = reason;

        _entryDisplay = entryDisplay;
    }

    public ManagedDataRootRole RootRole { get; }

    public ManagedTreeRefusalReason Reason { get; }

    public string RootRoleName => RoleName(RootRole);

    /// <summary>Creates content-free exception metadata and a proven lexical relative entry display.</summary>
    public static ManagedTreeInventoryRefusalException Create(
        ManagedDataRootRole role,
        ManagedTreeRefusalReason reason,
        string managedRoot,
        string entry)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(Enum.IsDefined(role), true, nameof(role));

        ArgumentOutOfRangeException.ThrowIfNotEqual(Enum.IsDefined(reason), true, nameof(reason));

        string display = "<unavailable>";

        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedRoot));

            string path = Path.GetFullPath(entry);

            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            // This is lexical proof only. Never resolve the failed entry or follow a symlink to
            // decide what may be displayed; an external entry gets no path display at all.
            if (string.Equals(root, path, comparison)
                || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, comparison))
            {
                display = RenderRelativeEntry(Path.GetRelativePath(root, path));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            // The failed external text and its exception are not diagnostic authority.
        }

        return new ManagedTreeInventoryRefusalException(role, reason, display);
    }

    public Error ToError() => new(ErrorCodes.Data.ManagedTreeRefused,
        "Factory-reset inventory refused managed root '" + RootRoleName + "', entry '" + _entryDisplay
        + "' (" + Reason + "). Inspect and restore the managed directory layout before planning again; preserve uncertain files and recovery evidence.");

    private static string RoleName(ManagedDataRootRole role) => role switch
    {
        ManagedDataRootRole.Attachments => "attachments",

        ManagedDataRootRole.Files => "files",

        ManagedDataRootRole.Logs => "logs",

        _ => "unknown",
    };

    private static string RenderRelativeEntry(string relative)
    {
        const int maximumCharacters = 240;

        System.Text.StringBuilder display = new();

        foreach (char character in relative)
        {
            System.Globalization.UnicodeCategory category = char.GetUnicodeCategory(character);

            string next = character == Path.DirectorySeparatorChar ? "/"
                : character == '\\' ? "\\\\"
                : character == '\'' || char.IsControl(character) || char.IsSurrogate(character)
                    || category is System.Globalization.UnicodeCategory.Format
                        or System.Globalization.UnicodeCategory.LineSeparator
                        or System.Globalization.UnicodeCategory.ParagraphSeparator
                    ? "\\u" + ((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)
                    : character.ToString();

            if (display.Length + next.Length > maximumCharacters - 3)
            {
                display.Append("...");

                break;
            }

            display.Append(next);
        }

        return display.ToString();
    }
}

/// <summary>Exact complete diagnostic totals with globally bounded preview samples.</summary>
public sealed record DataRetentionDiagnostics(
    long BlockerCount,
    long ConflictCount,
    DataRetentionReasonCount[] BlockerReasons,
    DataRetentionReasonCount[] ConflictReasons,
    long HoldingSessionCount,
    DataRetentionHoldingSession[] HoldingSessions,
    string SnapshotId,
    bool DetailsComplete,
    long OmittedBlockerCount = 0,
    long OmittedConflictCount = 0,
    long OmittedHoldingSessionCount = 0,
    DateTimeOffset ObservedAt = default);

public sealed record DataRetentionReasonCount(RetentionDataClass? DataClass, string Code, long Count);

public sealed record DataRetentionHoldingSession(Guid SessionId, long BlockerCount, long ConflictCount);

public sealed record DataRetentionPruneDiagnosticsRequest(
    [property: JsonRequired] DataRetentionRequest Request,
    [property: JsonRequired] string ExpectedPlanId,
    [property: JsonRequired] string ExpectedSnapshotId,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    string? Continuation = null,
    int Limit = 100);

public sealed record DataRetentionDiagnosticDetail(
    bool IsBlocker,
    RetentionDataClass? DataClass,
    string ResourceId,
    string Code,
    string Message,
    Guid? HoldingSessionId);

public sealed record DataRetentionDiagnosticPage(
    string PlanId,
    DateTimeOffset ObservedAt,
    long TotalCount,
    long BlockerCount,
    long ConflictCount,
    DataRetentionDiagnosticDetail[] Details,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextContinuation);

public sealed record DataRetentionDiagnosticPageAdmission(
    DataRetentionDiagnosticPage Page,
    ICovenantSnapshotReadLease? ReadLease);
