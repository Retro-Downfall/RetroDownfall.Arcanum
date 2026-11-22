using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal enum GuildLocalChildToolKind { DelegateTask, CreateAgentTask, SendAgentMessage, GetAgentTask, CancelAgentTask, ListAgents, SearchMemory,
    ReadFile, ListDirectory, SearchWorkspace, AcquireWorkspaceLease, RenewWorkspaceLease, ReleaseWorkspaceLease,
    WriteFile, EditFile, ApplyPatch,
    CreateCohort, AdmitCohortMember, CloseCohort, RemoveCohortMember, PostWorkOffer, SubmitBid, WithdrawBid, AwardWork, WithdrawWorkOffer, AppendSharedNote, ReadSharedNotes }

/// <summary>Host output consumed before generic model-tool stringification.</summary>
internal sealed record GuildLocalChildToolResult(GuildQuestDto Quest, string? Summary = null, ProviderCallSensitivity? Sensitivity = null, JsonElement? Projection = null,
    GuildAuthorityKind Authority = GuildAuthorityKind.Child, GuildWorkspaceReadDisclosure? WorkspaceDisclosure = null,
    ImmutableArray<string> ChangedWorkspacePaths = default);

internal interface IGuildLocalChildToolDispatcher
{
    bool SupportsLocal => true;

    bool SupportsRemote => false;

    bool SupportsMemory => false;

    bool SupportsWorkspaceReads => false;

    bool SupportsWorkspaceLeases => false;

    bool SupportsWorkspaceMutations => false;

    bool SupportsCohorts => false;

    Task<Result<GuildLocalChildToolResult>> DispatchAsync(GuildLocalChildToolCall call, CancellationToken cancellationToken);
}

internal sealed record GuildLocalChildToolCall(GuildLocalChildToolKind Kind, JsonElement Arguments,
    GuildQuestDto ParentQuest, GuildChildToolAdmission Admission);

/// <summary>Closed, host-owned eligibility shared by advertisement and retained tool admission.</summary>
internal sealed class GuildToolEligibility(bool disableAllTools, IOptions<ArcanumSettings> snapshot,
    IOptionsMonitor<ArcanumSettings>? monitor)
{
    internal bool IsEnabled() => !disableAllTools && (monitor?.CurrentValue ?? snapshot.Value).Features.Guild;
}

/// <summary>Only its issuing registry recognizes this nonwire call reference.</summary>
internal sealed class GuildChildToolAdmission(GuildLocalToolRegistry registry, GuildLocalToolRegistry.Invocation frame,
    JsonElement arguments, string argumentsDigest, GuildToolEligibility eligibility)
{
    private readonly object _sync = new();

    private string? _intent;

    private GuildQuestDto? _accepted;

    private JsonElement? _acceptedProjection;

    internal GuildLocalToolRegistry Registry { get; } = registry;

    internal GuildLocalToolRegistry.Invocation Frame { get; } = frame;

    internal JsonElement Arguments { get; } = arguments;

    internal string ArgumentsDigest { get; } = argumentsDigest;

    internal GuildToolEligibility Eligibility { get; } = eligibility;

    internal Guid IssuingStepId => Frame.IssuingStepId;

    internal Guid LogicalCallId => Frame.LogicalCallId;

    internal GuildToolGrant Tool => Frame.Tool;

    internal long ResponseAllocationBytes => Math.Max(0, Frame.OutputAllocationBytes - 4096);

    internal Result ValidateProjectionAllocation(JsonElement projection)
    {
        lock (Frame.Sync)
        {
            if (!Registry.Owns(this) || !ReferenceEquals(Registry.CaptureInvocation(Tool), Frame))
                return GuildLocalToolRegistry.Unavailable;

            return System.Text.Encoding.UTF8.GetByteCount(projection.GetRawText()) > ResponseAllocationBytes
                ? Result.Failure(new(ErrorCodes.Guild.PayloadTooLarge, "The typed tool projection exceeds one response allocation."))
                : Result.Success();
        }
    }

    internal GuildLocalChildToolKind Kind => Tool.SourceKind == GuildToolSourceKind.BuiltIn
        ? GuildLocalChildToolKind.DelegateTask : Tool.Name switch
        {
            "create_agent_task" => GuildLocalChildToolKind.CreateAgentTask,
            "send_agent_message" => GuildLocalChildToolKind.SendAgentMessage,
            "get_agent_task" => GuildLocalChildToolKind.GetAgentTask,
            "cancel_agent_task" => GuildLocalChildToolKind.CancelAgentTask,
            "list_agents" => GuildLocalChildToolKind.ListAgents,
            "search_memory" => GuildLocalChildToolKind.SearchMemory,
            "read_file" => GuildLocalChildToolKind.ReadFile,
            "list_directory" => GuildLocalChildToolKind.ListDirectory,
            "search_workspace" => GuildLocalChildToolKind.SearchWorkspace,
            "acquire_workspace_lease" => GuildLocalChildToolKind.AcquireWorkspaceLease,
            "renew_workspace_lease" => GuildLocalChildToolKind.RenewWorkspaceLease,
            "release_workspace_lease" => GuildLocalChildToolKind.ReleaseWorkspaceLease,
            "write_file" => GuildLocalChildToolKind.WriteFile,
            "edit_file" => GuildLocalChildToolKind.EditFile,
            "apply_patch" => GuildLocalChildToolKind.ApplyPatch,
            "create_cohort" => GuildLocalChildToolKind.CreateCohort,
            "admit_cohort_member" => GuildLocalChildToolKind.AdmitCohortMember,
            "close_cohort" => GuildLocalChildToolKind.CloseCohort,
            "remove_cohort_member" => GuildLocalChildToolKind.RemoveCohortMember,
            "post_work_offer" => GuildLocalChildToolKind.PostWorkOffer,
            "submit_bid" => GuildLocalChildToolKind.SubmitBid,
            "withdraw_bid" => GuildLocalChildToolKind.WithdrawBid,
            "award_work" => GuildLocalChildToolKind.AwardWork,
            "withdraw_work_offer" => GuildLocalChildToolKind.WithdrawWorkOffer,
            "append_shared_note" => GuildLocalChildToolKind.AppendSharedNote,
            "read_shared_notes" => GuildLocalChildToolKind.ReadSharedNotes,
            _ => throw new InvalidOperationException("Unknown task tool."),
        };

    internal bool BindIntent(string digest)
    {
        lock (_sync)
        {
            if (_intent is not null && _intent != digest) return false;

            _intent = digest;

            return true;
        }
    }

    internal bool MatchesIntent(string digest)
    {
        lock (_sync) return _intent == digest;
    }

    internal void RecordCommitted(GuildQuestDto accepted)
    {
        lock (_sync)
        {
            _accepted = accepted;

            _acceptedProjection = null;
        }
    }

    internal void RecordCommittedProjection(GuildQuestDto accepted, JsonElement projection)
    {
        lock (_sync)
        {
            _accepted = accepted;

            _acceptedProjection = projection.Clone();
        }
    }

    internal JsonElement? ReadCommittedProjection()
    {
        lock (_sync) return _acceptedProjection;
    }

    internal bool MatchesCommittedProjection(JsonElement? projection)
    {
        lock (_sync) return _acceptedProjection is { } accepted && projection is { } returned
            && accepted.GetRawText() == returned.GetRawText();
    }

    internal bool MatchesCommitted(GuildQuestDto returned)
    {
        lock (_sync)
            return _accepted is { } accepted && returned.Id == accepted.Id && returned.RunId == accepted.RunId
                && returned.SignetId == accepted.SignetId && returned.ParentQuestId == accepted.ParentQuestId
                && returned.RootQuestId == accepted.RootQuestId && returned.OperationId == accepted.OperationId;
    }
}

/// <summary>Closed local routes tied to one actual driver, accepted input and live host call scope.</summary>
internal sealed class GuildLocalToolRegistry(GuildLocalInferenceExecution parent, GuildLocalExecutionInput acceptedInput,
    IGuildLocalChildToolDispatcher? childDispatcher, long outputAllocationBytes = 1_048_576,
    IGuildWorkspaceMutationRuntime? workspaceMutations = null) : IDisposable
{
    private static readonly AsyncLocal<Invocation?> Current = new();

    private readonly ConditionalWeakTable<Invocation, object> _frames = new();

    private readonly ConditionalWeakTable<GuildChildToolAdmission, object> _issued = new();

    private readonly (CancellationTokenSource Source, CancellationToken Token) _lifetime = CreateLifetime();

    private readonly List<GuildWorkspaceReadDisclosure> _workspaceDisclosures = [];

    private readonly List<Guid> _retiredWorkspaceReads = [];

    private int _closed;

    internal static ImmutableArray<GuildToolGrant> SupportedTools { get; } =
        [new(GuildToolSourceKind.BuiltIn, "arcanum", "delegate_task"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "create_agent_task"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "send_agent_message"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "get_agent_task"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "cancel_agent_task"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "list_agents"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "search_memory"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "read_file"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "list_directory"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "search_workspace"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "acquire_workspace_lease"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "renew_workspace_lease"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "release_workspace_lease"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "write_file"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "edit_file"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "apply_patch"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "create_cohort"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "admit_cohort_member"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "close_cohort"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "remove_cohort_member"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "post_work_offer"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "submit_bid"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "withdraw_bid"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "award_work"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "withdraw_work_offer"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "append_shared_note"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "read_shared_notes")];

    internal GuildLocalInferenceExecution Parent { get; } = parent;

    internal GuildLocalExecutionInput AcceptedInput { get; } = acceptedInput;

    internal IGuildLocalChildToolDispatcher? ChildDispatcher { get; } = childDispatcher;

    internal IGuildWorkspaceMutationRuntime? WorkspaceMutations { get; } = workspaceMutations;

    internal long OutputAllocationBytes { get; } = Math.Max(0, outputAllocationBytes);

    internal bool IsActive => Volatile.Read(ref _closed) == 0;

    internal bool Supports(GuildToolGrant tool) => IsActive && Parent?.Store is not null && Parent.Driver is not null
        && AcceptedInput is not null && ChildDispatcher is not null && AcceptedInput.Mode == GuildLocalExecutionMode.Apprentice && SupportedTools.Contains(tool)
        && (tool.Name != "search_memory" || ChildDispatcher.SupportsMemory)
        && (!IsWorkspaceReadTool(tool.Name) || ChildDispatcher.SupportsWorkspaceReads)
        && (!IsWorkspaceLeaseTool(tool.Name) || ChildDispatcher.SupportsWorkspaceLeases)
        && (!IsWorkspaceMutationTool(tool.Name) || ChildDispatcher.SupportsWorkspaceMutations && WorkspaceMutations is not null)
        && (!IsCohortTool(tool.Name) || ChildDispatcher.SupportsCohorts)
        && (tool.Name is not ("create_agent_task" or "admit_cohort_member") || ChildDispatcher.SupportsLocal || ChildDispatcher.SupportsRemote);

    internal static bool IsCohortTool(string name) => name is "create_cohort" or "admit_cohort_member" or "close_cohort" or "remove_cohort_member" || IsMarketTool(name);

    internal static bool IsMarketTool(string name) => name is "post_work_offer" or "submit_bid" or "withdraw_bid" or "award_work" or "withdraw_work_offer" or "append_shared_note" or "read_shared_notes";

    internal static bool IsWorkspaceReadTool(string name) => name is "read_file" or "list_directory" or "search_workspace";

    internal static bool IsWorkspaceLeaseTool(string name) => name is "acquire_workspace_lease" or "renew_workspace_lease" or "release_workspace_lease";

    internal static bool IsWorkspaceMutationTool(string name) => name is "write_file" or "edit_file" or "apply_patch";

    internal GuildWorkspaceReadDisclosure[] CaptureWorkspaceDisclosures()
    {
        lock (_workspaceDisclosures) return [.. _workspaceDisclosures];
    }

    internal Guid[] TakeRetiredWorkspaceReads()
    {
        lock (_workspaceDisclosures)
        {
            Guid[] retired = [.. _retiredWorkspaceReads];

            _retiredWorkspaceReads.Clear();

            return retired;
        }
    }

    internal bool Matches(GuildExecutionHandle execution, IGuildToolAuthority authority) =>
        Parent?.Driver is { } driver && Parent.Store is not null && ReferenceEquals(driver.Execution, execution)
            && Parent.Store.OwnsToolAuthority(authority);

    internal bool Owns(GuildChildToolAdmission admission) => ReferenceEquals(admission.Registry, this)
        && IsActive && admission.Frame.IsActive && ReferenceEquals(admission.Frame.Registry, this)
        && _issued.TryGetValue(admission, out _) && Supports(admission.Tool) && admission.Eligibility.IsEnabled();

    internal IDisposable BeginInvocation(Guid issuingStepId, Guid logicalCallId, GuildToolGrant tool)
    {
        Invocation frame = new(this, issuingStepId, logicalCallId, tool);

        if (issuingStepId == Guid.Empty || logicalCallId == Guid.Empty || !Supports(tool)) frame.Close();

        else _frames.Add(frame, new());

        Invocation? previous = Current.Value;

        Current.Value = frame;

        return new Scope(frame, previous);
    }

    internal async Task<Result> AuthorizeAsync(GuildToolGrant tool, GuildToolEligibility eligibility, CancellationToken ct)
    {
        if (!Supports(tool) || !eligibility.IsEnabled()) return Unavailable;

        Result<GuildQuestStored> authorized = await Parent.Store.AuthorizeLocalToolAsync(this, tool, eligibility, ct).ConfigureAwait(false);

        return authorized.IsFailure ? authorized.Error : Result.Success();
    }

    internal Invocation? CaptureInvocation(GuildToolGrant tool)
    {
        Invocation? frame = Current.Value;

        return Supports(tool) && frame is { IsActive: true } && ReferenceEquals(frame.Registry, this)
            && frame.Tool == tool ? frame : null;
    }

    internal ValueTask<object?> DispatchAsync(GuildToolGrant tool, AIFunctionArguments arguments,
        GuildToolEligibility eligibility, CancellationToken ct) =>
        DispatchFrameAsync(Current.Value, tool, arguments, eligibility, ct);

    // The transport borrows the exact live frame; it neither creates nor closes a host invocation.
    internal async ValueTask<object?> DispatchTransferredAsync(Invocation frame, GuildToolGrant tool, AIFunctionArguments arguments,
        GuildToolEligibility eligibility, CancellationToken ct)
    {
        Invocation? previous = Current.Value;

        Current.Value = frame;

        try { return await DispatchFrameAsync(frame, tool, arguments, eligibility, ct).ConfigureAwait(false); }
        finally { Current.Value = previous; }
    }

    private async ValueTask<object?> DispatchFrameAsync(Invocation? frame, GuildToolGrant tool, AIFunctionArguments arguments,
        GuildToolEligibility eligibility, CancellationToken ct)
    {
        if (!Supports(tool) || !eligibility.IsEnabled() || frame is null || !frame.IsActive
            || !ReferenceEquals(frame.Registry, this) || frame.Tool != tool) return Format(Unavailable);

        Result<(JsonElement Arguments, string Digest)> frozen = Freeze(arguments);

        if (frozen.IsFailure) return Format(frozen.Error);

        GuildChildToolAdmission admission;

        lock (frame.Sync)
        {
            if (IsWorkspaceMutationTool(tool.Name) && frame.MutationClaimed) return Format(Conflict);

            if (frame.Admission is { } existing)
            {
                if (existing.ArgumentsDigest != frozen.Value.Digest) return Format(Conflict);

                admission = existing;
            }
            else
            {
                admission = new(this, frame, frozen.Value.Arguments, frozen.Value.Digest, eligibility);

                _issued.Add(admission, new());

                frame.Admission = admission;
            }

            if (IsWorkspaceMutationTool(tool.Name)) frame.MutationClaimed = true;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);

        Result<GuildQuestStored> authorized = await Parent.Store.AuthorizeLocalToolAsync(this, tool, eligibility, linked.Token).ConfigureAwait(false);

        if (authorized.IsFailure) return Format(authorized.Error);

        GuildLocalChildToolKind kind = admission.Kind;

        Result<GuildLocalChildToolResult> result = await ChildDispatcher!.DispatchAsync(
            new(kind, admission.Arguments, authorized.Value.Quest, admission), linked.Token).ConfigureAwait(false);

        if (result.IsFailure)
        {
            if (IsWorkspaceMutationTool(tool.Name) && result.Error.Code == ErrorCodes.Guild.ReconciliationRequired)
                frame.FatalError = result.Error;

            return Format(result.Error);
        }

        if (!Owns(admission) || !admission.MatchesCommitted(result.Value.Quest)) return Format(Unavailable);

        if ((IsCohortTool(tool.Name) || IsWorkspaceLeaseTool(tool.Name) || IsWorkspaceMutationTool(tool.Name))
            && !admission.MatchesCommittedProjection(result.Value.Projection)) return Format(Unavailable);

        if (result.Value.Projection is { } projection
            && System.Text.Encoding.UTF8.GetByteCount(projection.GetRawText()) > admission.ResponseAllocationBytes)
            return Format(new(ErrorCodes.Guild.PayloadTooLarge, "The typed tool projection exceeds one response allocation."));

        Result<GuildQuestStored> current = await Parent.Store.AuthorizeLocalToolAsync(this, tool, eligibility, linked.Token).ConfigureAwait(false);

        if (current.IsFailure) return Format(current.Error);

        if (!Owns(admission)) return Format(Unavailable);

        if (IsWorkspaceReadTool(tool.Name))
        {
            if (result.Value.WorkspaceDisclosure is not { } proof || !ReferenceEquals(proof.Registry, this)
                || proof.Tool != tool || !admission.MatchesCommittedProjection(result.Value.Projection)) return Format(Unavailable);

            lock (_workspaceDisclosures) _workspaceDisclosures.Add(proof);
        }

        if (!result.Value.ChangedWorkspacePaths.IsDefaultOrEmpty && IsWorkspaceMutationTool(tool.Name))
            RetireWorkspaceReads(result.Value.ChangedWorkspacePaths);

        return result.Value;
    }

    private void RetireWorkspaceReads(ImmutableArray<string> changedPaths)
    {
        lock (_workspaceDisclosures)
        {
            _workspaceDisclosures.RemoveAll(read =>
            {
                if (!read.SourcePaths.Any(path => changedPaths.Any(changed => Overlaps(path, changed)))) return false;

                _retiredWorkspaceReads.Add(read.LogicalCallId);

                return true;
            });
        }
    }

    private static bool Overlaps(string left, string right) => left == right
        || left.StartsWith(right + "/", StringComparison.Ordinal) || right.StartsWith(left + "/", StringComparison.Ordinal);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            try { _lifetime.Source.Cancel(); }
            catch (AggregateException) { }
            finally { _lifetime.Source.Dispose(); }
        }
    }

    private static (CancellationTokenSource Source, CancellationToken Token) CreateLifetime()
    {
        CancellationTokenSource source = new();

        return (source, source.Token);
    }

    internal sealed class Invocation(GuildLocalToolRegistry registry, Guid step, Guid call, GuildToolGrant tool)
    {
        private int _closed;

        internal object Sync { get; } = new();

        internal GuildLocalToolRegistry Registry { get; } = registry;

        internal Guid IssuingStepId { get; } = step;

        internal Guid LogicalCallId { get; } = call;

        internal GuildToolGrant Tool { get; } = tool;

        internal GuildChildToolAdmission? Admission;

        internal bool MutationClaimed;

        internal Error? FatalError;

        internal long OutputAllocationBytes { get; private set; } = registry.OutputAllocationBytes;

        internal void NarrowOutputAllocation(long allocationBytes)
        {
            lock (Sync) OutputAllocationBytes = Math.Min(OutputAllocationBytes, Math.Max(0, allocationBytes));
        }

        internal bool IsActive => Volatile.Read(ref _closed) == 0 && Registry.IsActive && Registry._frames.TryGetValue(this, out _);

        internal void Close() => Interlocked.Exchange(ref _closed, 1);
    }

    private sealed class Scope(Invocation? frame, Invocation? previous) : IDisposable
    {
        public void Dispose()
        {
            frame?.Close();

            if (frame is not null && ReferenceEquals(Current.Value, frame)) Current.Value = previous;
        }
    }

    private static Result<(JsonElement Arguments, string Digest)> Freeze(AIFunctionArguments arguments)
    {
        try
        {
            using MemoryStream buffer = new();

            using (Utf8JsonWriter writer = new(buffer)) WriteValue(writer, arguments, 0);

            byte[] canonical = ArcanumCanonicalJsonV1.Canonicalize(buffer.ToArray());

            using JsonDocument document = JsonDocument.Parse(canonical);

            return (document.RootElement.Clone(), Convert.ToHexString(SHA256.HashData(canonical)));
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or NotSupportedException)
        {
            return new Error(ErrorCodes.Guild.InvalidRequest, "Tool arguments must be closed JSON values.");
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value, int depth)
    {
        if (depth > 64) throw new JsonException("Tool JSON exceeds its parser depth.");

        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case JsonElement element: element.WriteTo(writer); break;
            case string text: writer.WriteStringValue(text); break;
            case bool boolean: writer.WriteBooleanValue(boolean); break;
            case int number: writer.WriteNumberValue(number); break;
            case long number: writer.WriteNumberValue(number); break;
            case double number: writer.WriteNumberValue(number); break;
            case decimal number: writer.WriteNumberValue(number); break;
            case IEnumerable<KeyValuePair<string, object?>> properties:
                writer.WriteStartObject();

                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Key);

                    WriteValue(writer, property.Value, depth + 1);
                }

                writer.WriteEndObject();

                break;
            case IEnumerable<object?> values:
                writer.WriteStartArray();

                foreach (object? item in values) WriteValue(writer, item, depth + 1);

                writer.WriteEndArray();

                break;
            default: throw new NotSupportedException("Unsupported tool JSON value.");
        }
    }

    private static string Format(Error error) => $"{error.Code}: {error.Message}";

    private static Error Conflict => new(ErrorCodes.Guild.IdempotencyConflict, "This host tool call already bound different arguments.");

    internal static Error Unavailable => new(ErrorCodes.Guild.ToolBoundaryUnavailable, "No qualified local tool effect is available.");
}
