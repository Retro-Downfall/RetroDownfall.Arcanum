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

    private static readonly AsyncLocal<Guid?> AsyncCurrent = new();

    private static readonly ConcurrentDictionary<string, Guid> SessionByConnectionRequest =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, Guid> SessionByOpaqueToken =
        new(StringComparer.Ordinal);

    /// <summary>Current session for attachment tool resolution on the inference async context.</summary>
    public static Guid? CurrentSessionId
    {
        get => AsyncCurrent.Value;
        set => AsyncCurrent.Value = value;
    }

    /// <summary>
    /// Binds <paramref name="sessionId"/> to an MCP JSON-RPC request id scoped by connection.
    /// Called at the client send boundary when the SDK request id is available.
    /// </summary>
    public static void BindRequest(string connectionKey, string requestId, Guid sessionId)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        SessionByConnectionRequest[ComposeRequestKey(connectionKey, requestId)] = sessionId;

    }

    /// <summary>Looks up a session bound to <paramref name="connectionKey"/> + <paramref name="requestId"/>.</summary>
    public static bool TryResolveRequest(string connectionKey, string requestId, out Guid sessionId)
    {

        if (string.IsNullOrWhiteSpace(connectionKey) || string.IsNullOrWhiteSpace(requestId))
        {
            sessionId = default;

            return false;
        }

        return SessionByConnectionRequest.TryGetValue(
            ComposeRequestKey(connectionKey, requestId),
            out sessionId);

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

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        SessionByOpaqueToken[token] = sessionId;

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

        return SessionByOpaqueToken.TryRemove(token, out sessionId);

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

    private static string ComposeRequestKey(string connectionKey, string requestId) =>
        connectionKey + "\u001f" + requestId;

}
