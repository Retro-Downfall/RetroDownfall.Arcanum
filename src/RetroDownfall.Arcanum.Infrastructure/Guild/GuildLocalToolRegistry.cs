using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal enum GuildLocalChildToolKind { DelegateTask, CreateAgentTask, SendAgentMessage, GetAgentTask, CancelAgentTask, ListAgents, SearchMemory }

/// <summary>Host output consumed before generic model-tool stringification.</summary>
internal sealed record GuildLocalChildToolResult(GuildQuestDto Quest, string? Summary = null, ProviderCallSensitivity? Sensitivity = null, JsonElement? Projection = null);

internal interface IGuildLocalChildToolDispatcher
{
    bool SupportsLocal => true;

    bool SupportsRemote => false;

    bool SupportsMemory => false;

    Task<Result<GuildLocalChildToolResult>> DispatchAsync(GuildLocalChildToolCall call, CancellationToken cancellationToken);
}

internal sealed record GuildLocalChildToolCall(GuildLocalChildToolKind Kind, JsonElement Arguments,
    GuildQuestDto ParentQuest, GuildChildToolAdmission Admission);

/// <summary>Only its issuing registry recognizes this nonwire call reference.</summary>
internal sealed class GuildChildToolAdmission(GuildLocalToolRegistry registry, GuildLocalToolRegistry.Invocation frame,
    JsonElement arguments, string argumentsDigest, Func<GuildToolGrant, bool> eligibility)
{
    private readonly object _sync = new();

    private string? _intent;

    private GuildQuestDto? _accepted;

    internal GuildLocalToolRegistry Registry { get; } = registry;

    internal GuildLocalToolRegistry.Invocation Frame { get; } = frame;

    internal JsonElement Arguments { get; } = arguments;

    internal string ArgumentsDigest { get; } = argumentsDigest;

    internal Func<GuildToolGrant, bool> Eligibility { get; } = eligibility;

    internal Guid IssuingStepId => Frame.IssuingStepId;

    internal Guid LogicalCallId => Frame.LogicalCallId;

    internal GuildToolGrant Tool => Frame.Tool;

    internal long ResponseAllocationBytes => Math.Max(0, Frame.OutputAllocationBytes - 4096);

    internal GuildLocalChildToolKind Kind => Tool.SourceKind == GuildToolSourceKind.BuiltIn
        ? GuildLocalChildToolKind.DelegateTask : Tool.Name switch
        {
            "create_agent_task" => GuildLocalChildToolKind.CreateAgentTask,
            "send_agent_message" => GuildLocalChildToolKind.SendAgentMessage,
            "get_agent_task" => GuildLocalChildToolKind.GetAgentTask,
            "cancel_agent_task" => GuildLocalChildToolKind.CancelAgentTask,
            "list_agents" => GuildLocalChildToolKind.ListAgents,
            "search_memory" => GuildLocalChildToolKind.SearchMemory,
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
        lock (_sync) _accepted = accepted;
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
    IGuildLocalChildToolDispatcher? childDispatcher, long outputAllocationBytes = 1_048_576) : IDisposable
{
    private static readonly AsyncLocal<Invocation?> Current = new();

    private readonly ConditionalWeakTable<GuildChildToolAdmission, object> _issued = new();

    private readonly (CancellationTokenSource Source, CancellationToken Token) _lifetime = CreateLifetime();

    private int _closed;

    internal static ImmutableArray<GuildToolGrant> SupportedTools { get; } =
        [new(GuildToolSourceKind.BuiltIn, "arcanum", "delegate_task"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "create_agent_task"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "send_agent_message"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "get_agent_task"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "cancel_agent_task"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "list_agents"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "search_memory")];

    internal GuildLocalInferenceExecution Parent { get; } = parent;

    internal GuildLocalExecutionInput AcceptedInput { get; } = acceptedInput;

    internal IGuildLocalChildToolDispatcher? ChildDispatcher { get; } = childDispatcher;

    internal long OutputAllocationBytes { get; } = Math.Max(0, outputAllocationBytes);

    internal bool IsActive => Volatile.Read(ref _closed) == 0;

    internal bool Supports(GuildToolGrant tool) => IsActive && Parent?.Store is not null && Parent.Driver is not null
        && AcceptedInput is not null && ChildDispatcher is not null && AcceptedInput.Mode == GuildLocalExecutionMode.Apprentice && SupportedTools.Contains(tool)
        && (tool.Name != "search_memory" || ChildDispatcher.SupportsMemory)
        && (tool.Name != "create_agent_task" || ChildDispatcher.SupportsLocal || ChildDispatcher.SupportsRemote);

    internal bool Matches(GuildExecutionHandle execution, IGuildToolAuthority authority) =>
        Parent?.Driver is { } driver && Parent.Store is not null && ReferenceEquals(driver.Execution, execution)
            && Parent.Store.OwnsToolAuthority(authority);

    internal bool Owns(GuildChildToolAdmission admission) => ReferenceEquals(admission.Registry, this)
        && IsActive && admission.Frame.IsActive && ReferenceEquals(admission.Frame.Registry, this)
        && _issued.TryGetValue(admission, out _) && Supports(admission.Tool) && admission.Eligibility(admission.Tool);

    internal IDisposable BeginInvocation(Guid issuingStepId, Guid logicalCallId, GuildToolGrant tool)
    {
        Invocation frame = new(this, issuingStepId, logicalCallId, tool);

        if (issuingStepId == Guid.Empty || logicalCallId == Guid.Empty || !Supports(tool)) frame.Close();

        Invocation? previous = Current.Value;

        Current.Value = frame;

        return new Scope(frame, previous);
    }

    internal async Task<Result> AuthorizeAsync(GuildToolGrant tool, Func<GuildToolGrant, bool> eligibility, CancellationToken ct)
    {
        if (!Supports(tool) || !eligibility(tool)) return Unavailable;

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
        Func<GuildToolGrant, bool> eligibility, CancellationToken ct) =>
        DispatchFrameAsync(Current.Value, tool, arguments, eligibility, ct);

    // The transport borrows the exact live frame; it neither creates nor closes a host invocation.
    internal async ValueTask<object?> DispatchTransferredAsync(Invocation frame, GuildToolGrant tool, AIFunctionArguments arguments,
        Func<GuildToolGrant, bool> eligibility, CancellationToken ct)
    {
        Invocation? previous = Current.Value;

        Current.Value = frame;

        try { return await DispatchFrameAsync(frame, tool, arguments, eligibility, ct).ConfigureAwait(false); }
        finally { Current.Value = previous; }
    }

    private async ValueTask<object?> DispatchFrameAsync(Invocation? frame, GuildToolGrant tool, AIFunctionArguments arguments,
        Func<GuildToolGrant, bool> eligibility, CancellationToken ct)
    {
        if (!Supports(tool) || !eligibility(tool) || frame is null || !frame.IsActive
            || !ReferenceEquals(frame.Registry, this) || frame.Tool != tool) return Format(Unavailable);

        Result<(JsonElement Arguments, string Digest)> frozen = Freeze(arguments);

        if (frozen.IsFailure) return Format(frozen.Error);

        GuildChildToolAdmission admission;

        lock (frame.Sync)
        {
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
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);

        Result<GuildQuestStored> authorized = await Parent.Store.AuthorizeLocalToolAsync(this, tool, eligibility, linked.Token).ConfigureAwait(false);

        if (authorized.IsFailure) return Format(authorized.Error);

        GuildLocalChildToolKind kind = admission.Kind;

        Result<GuildLocalChildToolResult> result = await ChildDispatcher!.DispatchAsync(
            new(kind, admission.Arguments, authorized.Value.Quest, admission), linked.Token).ConfigureAwait(false);

        if (result.IsFailure) return Format(result.Error);

        if (!Owns(admission) || !admission.MatchesCommitted(result.Value.Quest)) return Format(Unavailable);

        if (result.Value.Projection is { } projection
            && System.Text.Encoding.UTF8.GetByteCount(projection.GetRawText()) > admission.ResponseAllocationBytes)
            return Format(new(ErrorCodes.Guild.PayloadTooLarge, "The typed tool projection exceeds one response allocation."));

        Result<GuildQuestStored> current = await Parent.Store.AuthorizeLocalToolAsync(this, tool, eligibility, linked.Token).ConfigureAwait(false);

        return current.IsFailure ? Format(current.Error) : result.Value;
    }

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

        internal long OutputAllocationBytes { get; private set; } = registry.OutputAllocationBytes;

        internal void NarrowOutputAllocation(long allocationBytes)
        {
            lock (Sync) OutputAllocationBytes = Math.Min(OutputAllocationBytes, Math.Max(0, allocationBytes));
        }

        internal bool IsActive => Volatile.Read(ref _closed) == 0 && Registry.IsActive;

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
