namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Carries the taken Covenant capability from the server's dispatch point into its handler.
/// </summary>
/// <remarks>
/// The <c>AsyncLocal</c> is deliberately only the last hop. The capability was already resolved from
/// the connection-and-request-id table on the server's own task, so a child task that starts after
/// disposal, or resumes during closing, finds a capability that refuses it rather than a stale
/// ambient it can still use. An <c>AsyncLocal</c> without that request bridge would be the bug: the
/// in-process server runs on another task and concurrent requests have to stay isolated (§10.14).
/// </remarks>
internal static class CovenantToolInvocationAmbient
{

    private static readonly AsyncLocal<CovenantToolCapabilityGrant?> Ambient = new();

    internal static CovenantToolCapabilityGrant? Current
    {

        get => Ambient.Value;

        set => Ambient.Value = value;

    }

}
