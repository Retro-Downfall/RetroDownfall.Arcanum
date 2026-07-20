using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Ambient current-session id for the <c>attach_session_file</c> MCP tool.
/// Set by <c>WizardIntelligenceProvider</c> around the tool loop (via <see cref="AsyncLocal{T}"/>)
/// for post-tool injection on the inference async context. In-process MCP <c>tools/call</c>
/// handlers run on a separate <see cref="Task"/> and resolve the session via a per-connection
/// JSON-RPC request-id map (preferred) or a host opaque invocation token (fallback) — never via
/// a process-wide fallback, tool name, or model-supplied session id.
/// </summary>
public static class SessionAttachmentToolAmbient
{

    /// <summary>
    /// Host-only argument name for the opaque invocation token fallback. Excluded from the tool
    /// schema; overwritten at the client send boundary; stripped server-side before tool logic;
    /// never audited or persisted.
    /// </summary>
    public const string OpaqueInvocationTokenArgumentName = "_arcanumHostInvocation";

    /// <summary>
    /// Default lifetime for request-id and opaque-token bindings. Abandoned binds (send failure,
    /// never delivered tools/call) are swept after this interval so maps cannot grow forever.
    /// </summary>
    public static readonly TimeSpan DefaultBindingTtl = TimeSpan.FromMinutes(10);

    private static readonly AsyncLocal<Guid?> AsyncCurrent = new();

    private static readonly ConcurrentDictionary<string, AmbientBinding> SessionByConnectionRequest =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, AmbientBinding> SessionByOpaqueToken =
        new(StringComparer.Ordinal);

    private static TimeSpan _bindingTtl = DefaultBindingTtl;

    private static Func<long> _utcTicksNow = static () => DateTime.UtcNow.Ticks;

    private readonly record struct AmbientBinding(Guid SessionId, long CreatedUtcTicks);

    /// <summary>Current session for attachment tool resolution on the inference async context.</summary>
    public static Guid? CurrentSessionId
    {
        get => AsyncCurrent.Value;
        set => AsyncCurrent.Value = value;
    }

    /// <summary>Test seam: override binding TTL. Call <see cref="ResetTestSeams"/> in teardown.</summary>
    internal static void SetBindingTtlForTests(TimeSpan ttl) => _bindingTtl = ttl;

    /// <summary>Test seam: override the clock used for TTL. Call <see cref="ResetTestSeams"/> in teardown.</summary>
    internal static void SetUtcTicksNowForTests(Func<long> utcTicksNow) =>
        _utcTicksNow = utcTicksNow ?? throw new ArgumentNullException(nameof(utcTicksNow));

    /// <summary>Test seam: restore production TTL/clock defaults and clear maps.</summary>
    internal static void ResetTestSeams()
    {
        _bindingTtl = DefaultBindingTtl;
        _utcTicksNow = static () => DateTime.UtcNow.Ticks;
        SessionByConnectionRequest.Clear();
        SessionByOpaqueToken.Clear();
    }

    /// <summary>Test hook: count of live request-id bindings (after opportunistic sweep).</summary>
    internal static int RequestBindingCountForTests
    {
        get
        {
            SweepExpired();
            return SessionByConnectionRequest.Count;
        }
    }

    /// <summary>Test hook: count of live opaque-token bindings (after opportunistic sweep).</summary>
    internal static int OpaqueTokenCountForTests
    {
        get
        {
            SweepExpired();
            return SessionByOpaqueToken.Count;
        }
    }

    /// <summary>
    /// Binds <paramref name="sessionId"/> to an MCP JSON-RPC request id scoped by connection.
    /// Called at the client send boundary when the SDK request id is available.
    /// </summary>
    public static void BindRequest(string connectionKey, string requestId, Guid sessionId)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        SweepExpired();

        SessionByConnectionRequest[ComposeRequestKey(connectionKey, requestId)] =
            new AmbientBinding(sessionId, _utcTicksNow());

    }

    /// <summary>Looks up a session bound to <paramref name="connectionKey"/> + <paramref name="requestId"/>.</summary>
    public static bool TryResolveRequest(string connectionKey, string requestId, out Guid sessionId)
    {

        if (string.IsNullOrWhiteSpace(connectionKey) || string.IsNullOrWhiteSpace(requestId))
        {
            sessionId = default;

            return false;
        }

        SweepExpired();

        string key = ComposeRequestKey(connectionKey, requestId);

        if (!SessionByConnectionRequest.TryGetValue(key, out AmbientBinding binding))
        {
            sessionId = default;

            return false;
        }

        if (IsExpired(binding))
        {
            SessionByConnectionRequest.TryRemove(key, out _);
            sessionId = default;

            return false;
        }

        sessionId = binding.SessionId;

        return true;

    }

    /// <summary>Removes a request-id binding (call from <c>finally</c> after tools/call completes).</summary>
    public static void UnbindRequest(string connectionKey, string requestId)
    {

        if (string.IsNullOrWhiteSpace(connectionKey) || string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        SessionByConnectionRequest.TryRemove(ComposeRequestKey(connectionKey, requestId), out _);

    }

    /// <summary>
    /// Creates a cryptographically random opaque token bound to <paramref name="sessionId"/>.
    /// Used only when the SDK JSON-RPC request id is unavailable at send time.
    /// </summary>
    public static string CreateAndBindOpaqueToken(Guid sessionId)
    {

        SweepExpired();

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        SessionByOpaqueToken[token] = new AmbientBinding(sessionId, _utcTicksNow());

        return token;

    }

    /// <summary>
    /// Takes (removes) an opaque token binding. Returns <see langword="false"/> when unknown.
    /// </summary>
    public static bool TryTakeOpaqueToken(string? token, out Guid sessionId)
    {

        if (string.IsNullOrWhiteSpace(token))
        {
            sessionId = default;

            return false;
        }

        SweepExpired();

        if (!SessionByOpaqueToken.TryRemove(token, out AmbientBinding binding))
        {
            sessionId = default;

            return false;
        }

        if (IsExpired(binding))
        {
            sessionId = default;

            return false;
        }

        sessionId = binding.SessionId;

        return true;

    }

    /// <summary>Best-effort remove without taking (tests / abandoned sends).</summary>
    public static void ForgetOpaqueToken(string? token)
    {

        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        SessionByOpaqueToken.TryRemove(token, out _);

    }

    /// <summary>Removes expired request-id and opaque-token bindings.</summary>
    public static void SweepExpired()
    {
        long now = _utcTicksNow();
        long ttlTicks = _bindingTtl.Ticks;

        foreach (KeyValuePair<string, AmbientBinding> pair in SessionByConnectionRequest)
        {
            if (now - pair.Value.CreatedUtcTicks > ttlTicks)
            {
                SessionByConnectionRequest.TryRemove(pair.Key, out _);
            }
        }

        foreach (KeyValuePair<string, AmbientBinding> pair in SessionByOpaqueToken)
        {
            if (now - pair.Value.CreatedUtcTicks > ttlTicks)
            {
                SessionByOpaqueToken.TryRemove(pair.Key, out _);
            }
        }
    }

    private static bool IsExpired(AmbientBinding binding) =>
        _utcTicksNow() - binding.CreatedUtcTicks > _bindingTtl.Ticks;

    private static string ComposeRequestKey(string connectionKey, string requestId) =>
        connectionKey + "\u001f" + requestId;

}
