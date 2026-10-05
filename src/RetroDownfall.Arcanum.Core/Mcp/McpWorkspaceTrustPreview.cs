namespace RetroDownfall.Arcanum.Core.Mcp;

/// <summary>
/// What a workspace-local <c>mcp.json</c> would be allowed to run, as read by the host that would trust
/// it, for the operator to read before approving.
/// </summary>
/// <remarks>
/// Trust is a grant to launch whatever commands the file names. The host builds this from the same
/// bytes it digests when it records trust, and the approval carries <see cref="ConfigDigest"/> back, so
/// the host refuses a trust request whose file changed after it was previewed: the operator approves the
/// text they were shown or nothing. Every authored string is made safe to print before it leaves the host.
/// </remarks>
/// <param name="Workspace">The workspace root as the host normalized it, made safe to print.</param>
/// <param name="Lines">The preview, one line per entry: each server with its transport and command line or URL.</param>
/// <param name="Truncated">
/// True when at least one authored field was longer than the preview shows. A preview that leaves text out
/// cannot be the basis for approving it, so a client must not offer approval when this is set.
/// </param>
/// <param name="ConfigDigest">The SHA-256 (upper-case hex) of the exact <c>mcp.json</c> bytes this preview describes.</param>
public sealed record McpWorkspaceTrustPreview(
    string Workspace,
    string[] Lines,
    bool Truncated,
    string ConfigDigest);
