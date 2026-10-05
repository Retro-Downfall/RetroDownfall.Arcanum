namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Model-visible account of a tool child whose macOS memory monitor stopped while it was running
/// (<see cref="CappedChildProcessOutcome.MemoryMonitorStopped"/>).
/// </summary>
/// <remarks>
/// One wording for every tool that runs a child, because the point of it is what it does <em>not</em>
/// say: this is not a refusal to start. The child ran until the monitor stopped, so a model told that
/// the invocation "was blocked" or "never started" may run it again on top of work that already happened.
/// </remarks>
internal static class ChildProcessMemoryMonitorMessages
{
    internal static string Describe(string toolName) =>
        $"{toolName}: the process started and ran, but the monitor enforcing its memory limit stopped while it was running, so the limit stopped being enforced and the process tree was killed. "
        + "Its work may be partly done and its output is incomplete; check what it changed before running it again.";
}
