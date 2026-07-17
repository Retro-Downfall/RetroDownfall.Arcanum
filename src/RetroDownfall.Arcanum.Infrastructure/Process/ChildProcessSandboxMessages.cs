namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Model-visible denial when a child-process filesystem sandbox cannot be applied and
/// <c>Arcanum:Security:AllowUnsandboxedToolChildren</c> is false.
/// </summary>
internal static class ChildProcessSandboxMessages
{

    internal const string SandboxUnavailable =
        "Child process filesystem sandbox is unavailable on this host; refusing to run tool unbounded.";

    internal const string WindowsSanctumPathBoundaryDenied =
        "Child process filesystem sandbox is unavailable on Windows while Sanctum path-boundary enforcement is enabled; refusing to run tool unbounded.";

    internal const string NotNetworkIsolationNote =
        "Filesystem sandbox only — does not isolate network use by child binaries.";

}
