using System.Collections.Concurrent;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// One taken capability and the nonce every later operation on it must present.
/// </summary>
public sealed record CovenantToolCapabilityGrant(
    CovenantToolInvocationContext Capability,
    CovenantToolCapabilityNonce Nonce);

/// <summary>
/// The connection-and-request-id table that carries a Covenant capability to its MCP handler.
/// </summary>
/// <remarks>
/// The in-process server runs on its own task, so the turn's async flow does not reach a handler and
/// an <c>AsyncLocal</c> would either vanish or leak between concurrent requests. This table is the
/// bridge, and every rule in it exists because JSON-RPC request ids are reused over a long-lived
/// connection (§10.14):
///
/// <list type="bullet">
/// <item>registration is <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/> only, so a duplicate
/// id is refused rather than overwriting a live capability;</item>
/// <item>taking is one-shot and delegated to the capability itself, so two racing handlers cannot
/// both believe they own it;</item>
/// <item>the id stays reserved until the handler removes it, so a second call cannot install a
/// capability while the first is still draining;</item>
/// <item>removal compares the key <em>and</em> the exact registration, so an earlier request's
/// delayed cleanup cannot free the id a later request now owns;</item>
/// <item>the TTL sweep reclaims only registrations nobody ever took — a taken handler owns its own
/// bounded cancellation lifetime and must not have its capability pulled out from under it.</item>
/// </list>
/// </remarks>
public sealed class CovenantToolCapabilityRegistry
{

    private readonly ConcurrentDictionary<string, Registration> _byRequest = new(StringComparer.Ordinal);

    private readonly long _ttlTicks;

    private readonly Func<long> _timestamp;

    public CovenantToolCapabilityRegistry(TimeSpan ttl, Func<long> timestamp)
    {

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);

        ArgumentNullException.ThrowIfNull(timestamp);

        _ttlTicks = ttl.Ticks;

        _timestamp = timestamp;

    }

    public CovenantToolCapabilityRegistry()
        : this(TimeSpan.FromMinutes(10), static () => DateTime.UtcNow.Ticks)
    {
    }

    internal int CountForTests => _byRequest.Count;

    /// <summary>
    /// Installs one capability for one exact request, refusing a duplicate rather than replacing it.
    /// </summary>
    public bool TryRegister(
        string connectionKey,
        string requestId,
        CovenantToolInvocationContext capability,
        CovenantToolCapabilityNonce nonce)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);

        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        ArgumentNullException.ThrowIfNull(capability);

        if (!nonce.IsValid)
        {

            throw new ArgumentException(
                "A Covenant capability registration requires a minted capability nonce.",
                nameof(nonce));

        }

        return _byRequest.TryAdd(
            ComposeKey(connectionKey, requestId),
            new Registration(capability, nonce, _timestamp()));

    }

    /// <summary>Hands the registered capability to exactly one handler.</summary>
    public Result<CovenantToolCapabilityGrant> TryTake(string connectionKey, string requestId)
    {

        if (string.IsNullOrWhiteSpace(connectionKey) || string.IsNullOrWhiteSpace(requestId))
        {
            return Result<CovenantToolCapabilityGrant>.Failure(MissingError());
        }

        if (!_byRequest.TryGetValue(ComposeKey(connectionKey, requestId), out Registration? registration))
        {
            return Result<CovenantToolCapabilityGrant>.Failure(MissingError());
        }

        Result taken = registration.Capability.TryTake(registration.Nonce);

        return taken.IsFailure
            ? Result<CovenantToolCapabilityGrant>.Failure(taken.Error)
            : Result<CovenantToolCapabilityGrant>.Success(
                new CovenantToolCapabilityGrant(registration.Capability, registration.Nonce));

    }

    /// <summary>
    /// Frees one request id, but only for the caller that holds the registration's own nonce.
    /// </summary>
    public bool Remove(string connectionKey, string requestId, CovenantToolCapabilityNonce nonce)
    {

        if (string.IsNullOrWhiteSpace(connectionKey) || string.IsNullOrWhiteSpace(requestId))
        {
            return false;
        }

        string key = ComposeKey(connectionKey, requestId);

        if (!_byRequest.TryGetValue(key, out Registration? registration)
            || !registration.Nonce.Equals(nonce))
        {
            return false;
        }

        return RemoveExact(key, registration);

    }

    /// <summary>
    /// Releases one connection-and-request-id's registration when the frame that would have carried
    /// it to a handler never left the client — a failed transport send, before any handler could
    /// have reached <see cref="TryTake"/> for this id. No nonce is required: the caller here is the
    /// send path itself, not a handler that took the capability.
    /// </summary>
    /// <remarks>
    /// A no-op once the registration has been taken (mirrors the state guard in
    /// <see cref="SweepExpired"/>), so a handler that is still draining a capability it already took
    /// keeps its own bounded cancellation lifetime.
    /// </remarks>
    public bool ReleaseUnsent(string connectionKey, string requestId)
    {

        if (string.IsNullOrWhiteSpace(connectionKey) || string.IsNullOrWhiteSpace(requestId))
        {
            return false;
        }

        string key = ComposeKey(connectionKey, requestId);

        if (!_byRequest.TryGetValue(key, out Registration? registration)
            || registration.Capability.State != CovenantToolCapabilityState.Registered)
        {
            return false;
        }

        return RemoveExact(key, registration);

    }

    /// <summary>
    /// Reclaims registrations that were installed and never taken, returning them for disposal.
    /// </summary>
    public IReadOnlyList<CovenantToolInvocationContext> SweepExpired()
    {

        long now = _timestamp();

        List<CovenantToolInvocationContext> reclaimed = [];

        foreach (KeyValuePair<string, Registration> pair in _byRequest)
        {

            if (now - pair.Value.CreatedTimestamp <= _ttlTicks
                || pair.Value.Capability.State != CovenantToolCapabilityState.Registered)
            {
                continue;
            }

            if (RemoveExact(pair.Key, pair.Value))
            {
                reclaimed.Add(pair.Value.Capability);
            }

        }

        return reclaimed;

    }

    private bool RemoveExact(string key, Registration registration) =>
        ((ICollection<KeyValuePair<string, Registration>>)_byRequest)
            .Remove(new KeyValuePair<string, Registration>(key, registration));

    private static Error MissingError() =>
        new(
            ErrorCodes.Covenant.LifecycleConflict,
            "No Covenant tool capability is registered for this MCP request.");

    /// <summary>
    /// The ASCII unit separator keeps the pair unambiguous: neither a connection key nor a
    /// JSON-RPC request id may contain it, so no two different pairs compose the same table key.
    /// </summary>
    private static string ComposeKey(string connectionKey, string requestId) =>
        connectionKey + "\u001f" + requestId;

    /// <summary>
    /// Reference identity by design: <see cref="RemoveExact"/> depends on the default comparer, so a
    /// value type or an overridden equality would make two distinct registrations interchangeable.
    /// </summary>
    private sealed class Registration(
        CovenantToolInvocationContext capability,
        CovenantToolCapabilityNonce nonce,
        long createdTimestamp)
    {

        public CovenantToolInvocationContext Capability { get; } = capability;

        public CovenantToolCapabilityNonce Nonce { get; } = nonce;

        public long CreatedTimestamp { get; } = createdTimestamp;

    }

}
