using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Host-only closed tool binding. The host eligibility probe intersects current availability,
/// attunement, edition and invocation policy. Unsupported resource/effect routes remain unavailable.
/// </summary>
internal sealed class GuildToolInvocationContext(
    GuildExecutionHandle execution,
    IGuildToolAuthority authority,
    Func<GuildToolGrant, bool> currentEligibility, GuildLocalToolRegistry? registry = null)
{
    internal GuildLocalToolRegistry? Registry => registry;

    internal ValueTask<object?> InvokeBoundAsync(AIFunction function, AIFunctionArguments arguments, CancellationToken ct) =>
        function is BoundFunction bound && ReferenceEquals(bound.Context, this)
            ? InvokeAsync(bound.Tool, arguments, ct) : ValueTask.FromResult<object?>(Format(Unavailable));

    private ValueTask<object?> InvokeAsync(GuildToolGrant tool, AIFunctionArguments arguments, CancellationToken ct)
    {
        if (!currentEligibility(tool)) return ValueTask.FromResult<object?>(Format(new(ErrorCodes.Guild.ToolNotGranted, "The tool is unavailable under the current host policy.")));

        return registry is not null && registry.Matches(execution, authority) && registry.Supports(tool)
            ? registry.DispatchAsync(tool, arguments, currentEligibility, ct) : RefuseBoundAsync(tool, ct);
    }

    private async ValueTask<object?> RefuseBoundAsync(GuildToolGrant tool, CancellationToken ct) => Format(await RefuseDispatchAsync(tool, ct).ConfigureAwait(false));

    internal GuildLocalToolRegistry.Invocation? CaptureMcpInvocation(string name)
    {
        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", name);

        return registry is not null && registry.Matches(execution, authority) && currentEligibility(tool)
            ? registry.CaptureInvocation(tool) : null;
    }

    internal ValueTask<object?> DispatchMcpAsync(GuildLocalToolRegistry.Invocation frame, string name,
        JsonElement arguments, CancellationToken ct, long outputAllocationBytes = 1_048_576)
    {
        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", name);

        if (registry is null || !registry.Matches(execution, authority) || !ReferenceEquals(frame.Registry, registry)
            || frame.Tool != tool || !frame.IsActive || !currentEligibility(tool))
            return ValueTask.FromResult<object?>(Format(Unavailable));

        frame.NarrowOutputAllocation(outputAllocationBytes);

        if (arguments.ValueKind != JsonValueKind.Object)
            return ValueTask.FromResult<object?>(Format(new(ErrorCodes.Guild.InvalidRequest, "Tool arguments must be a JSON object.")));

        AIFunctionArguments values = new();

        foreach (JsonProperty property in arguments.EnumerateObject())
        {
            if (!values.TryAdd(property.Name, property.Value.Clone()))
                return ValueTask.FromResult<object?>(Format(new(ErrorCodes.Guild.InvalidRequest, "Tool argument names must be unique.")));
        }

        return registry.DispatchTransferredAsync(frame, tool, values, currentEligibility, ct);
    }

    internal AIFunction Bind(AIFunction function, GuildToolGrant tool)
    {
        ArgumentNullException.ThrowIfNull(function);

        ArgumentNullException.ThrowIfNull(tool);

        if (!string.Equals(function.Name, tool.Name, StringComparison.Ordinal))
        {
            throw new ArgumentException("A Guild tool binding must name the exact function.", nameof(tool));
        }

        return function is McpBridgeTool bridge && (registry is null || !registry.Supports(tool))
            ? bridge.WithGuildAuthority(this, tool)
            : new BoundFunction(function, this, tool);
    }

    internal async Task<AIFunction?> AdvertiseAsync(AIFunction function, GuildToolGrant tool, CancellationToken cancellationToken)
    {
        _ = Bind(function, tool);

        if (!currentEligibility(tool))
        {
            return null;
        }

        if (registry is not null && registry.Matches(execution, authority) && registry.Supports(tool))
        {
            Result qualified = await registry.AuthorizeAsync(tool, currentEligibility, cancellationToken).ConfigureAwait(false);

            return qualified.IsSuccess ? Bind(function, tool) : null;
        }

        Result result = await authority.VerifyToolAsync(execution, tool, boundaryAvailable: false, cancellationToken).ConfigureAwait(false);

        // No installed resource/effect adapter means no advertised capability, even if a faulty
        // authority implementation were to return success for boundaryAvailable:false.
        _ = result;

        return null;
    }

    internal async Task<string> RefuseFunctionAsync(AIFunction function, CancellationToken cancellationToken)
    {
        if (function is BoundFunction bound && ReferenceEquals(bound.Context, this))
        {
            return Format(await RefuseDispatchAsync(bound.Tool, cancellationToken).ConfigureAwait(false));
        }

        if (function is McpBridgeTool bridge && bridge.TryGetGuildBinding(this, out GuildToolGrant? tool))
        {
            return Format(await RefuseDispatchAsync(tool!, cancellationToken).ConfigureAwait(false));
        }

        return Format(Unavailable);
    }

    internal async Task<Error> RefuseDispatchAsync(GuildToolGrant tool, CancellationToken cancellationToken)
    {
        if (!currentEligibility(tool))
        {
            return new(ErrorCodes.Guild.ToolNotGranted, "The tool is unavailable under the current host policy.");
        }

        Result<object?> result = await authority.AdmitToolAsync<object?>(execution, tool, boundaryAvailable: false,
            static (_, _) => Task.FromResult(Result<object?>.Failure(Unavailable)), cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? result.Error : Unavailable;
    }

    internal static bool TryRefuseNative(string canonicalName, CancellationToken cancellationToken, out ValueTask<object?> refusal)
    {
        if (GuildToolInvocationAmbient.Current is not { } context)
        {
            refusal = default;

            return false;
        }

        refusal = RefuseNativeAsync(context, canonicalName, cancellationToken);

        return true;
    }

    private static async ValueTask<object?> RefuseNativeAsync(GuildToolInvocationContext context, string canonicalName,
        CancellationToken cancellationToken) => Format(await context.RefuseDispatchAsync(
            new(GuildToolSourceKind.BuiltIn, "arcanum", canonicalName), cancellationToken).ConfigureAwait(false));

    internal static Error Unavailable => new(ErrorCodes.Guild.ToolBoundaryUnavailable,
        "No host-owned Guild resource/effect adapter is installed for this tool.");

    internal static string Format(Error error) => $"{error.Code}: {error.Message}";

    private sealed class BoundFunction(AIFunction function, GuildToolInvocationContext context, GuildToolGrant tool) : AIFunction
    {
        internal GuildToolInvocationContext Context => context;

        internal GuildToolGrant Tool => tool;

        public override string Name => tool.Name;

        public override string Description => function.Description;

        public override JsonElement JsonSchema => function.JsonSchema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            context.InvokeAsync(tool, arguments, cancellationToken);
    }
}

/// <summary>Host call scope, never an argument or protocol credential.</summary>
internal static class GuildToolInvocationAmbient
{
    private static readonly AsyncLocal<GuildToolInvocationContext?> Value = new();

    internal static GuildToolInvocationContext? Current
    {
        get => Value.Value;
        set => Value.Value = value;
    }

    internal static IDisposable Begin(GuildToolInvocationContext context)
    {
        GuildToolInvocationContext? previous = Current;

        Current = context;

        return new Scope(previous);
    }

    private sealed class Scope(GuildToolInvocationContext? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                Current = previous;
            }
        }
    }
}

/// <summary>Transfers private host context across the in-process server task, bound to the exact request and name.</summary>
internal static class GuildToolInvocationBinding
{
    internal sealed record Reservation(GuildToolInvocationContext? Context, string? Name,
        GuildToolRequestBindingNonce Nonce, bool Claimed = false, GuildLocalToolRegistry.Invocation? Frame = null);

    private static readonly ConcurrentDictionary<(string Connection, string Request), Reservation> Bindings = new();

    internal static GuildToolRequestBindingNonce BindRequest(string connection, string request, string? name, GuildToolInvocationContext? context)
    {
        GuildToolRequestBindingNonce nonce = new();

        // Ordinary requests reserve their identity too: a later Guild call must not attach itself
        // to an ordinary call that is still queued or executing on this connection.
        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(request)
            || (context is not null && string.IsNullOrWhiteSpace(name))
            || !Bindings.TryAdd((connection, request), new(context, name, nonce, Frame: name is null ? null : context?.CaptureMcpInvocation(name))))
        {
            throw new InvalidOperationException("The private tool request identity is unavailable.");
        }

        return nonce;
    }

    internal static Reservation? CaptureRequest(string connection, string request)
    {
        while (Bindings.TryGetValue((connection, request), out Reservation? reservation))
        {
            if (reservation.Claimed)
            {
                return reservation;
            }

            Reservation claimed = reservation with { Claimed = true };

            if (Bindings.TryUpdate((connection, request), claimed, reservation))
            {
                return claimed;
            }
        }

        return null;
    }

    internal static void CompleteRequest(string connection, string request, GuildToolRequestBindingNonce nonce)
    {
        if (Bindings.TryGetValue((connection, request), out Reservation? reservation) && ReferenceEquals(reservation.Nonce, nonce))
        {
            ((ICollection<KeyValuePair<(string, string), Reservation>>)Bindings).Remove(new((connection, request), reservation));
        }
    }

    internal static void UnbindFailedRequest(string connection, string request, GuildToolRequestBindingNonce nonce)
    {
        if (Bindings.TryGetValue((connection, request), out Reservation? reservation)
            && !reservation.Claimed && ReferenceEquals(reservation.Nonce, nonce))
        {
            // Claiming replaces the immutable reservation. A concurrent server claim makes this
            // exact-value removal fail, so a late send failure cannot free an admitted identity.
            ((ICollection<KeyValuePair<(string, string), Reservation>>)Bindings).Remove(new((connection, request), reservation));
        }
    }

    internal static void UnbindConnection(string connection)
    {
        foreach (var pair in Bindings)
        {
            if (string.Equals(pair.Key.Connection, connection, StringComparison.Ordinal))
            {
                ((ICollection<KeyValuePair<(string, string), Reservation>>)Bindings).Remove(pair);
            }
        }
    }
}

/// <summary>Exact send ownership, distinct from execution authority and never serialized.</summary>
internal sealed class GuildToolRequestBindingNonce
{
}
