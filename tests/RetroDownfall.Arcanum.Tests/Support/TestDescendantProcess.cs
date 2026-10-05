using System.ComponentModel;
using System.Globalization;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Best-effort cleanup of a process a test deliberately left running (a held-open pipe, an orphaned
/// descendant). The process may never have started, may have written no pid file, or may already be gone;
/// each of those is the outcome the test wants, so it is tolerated. Anything else is a real failure of the
/// cleanup and propagates instead of masking the assertion the test's own <c>finally</c> is running for.
/// </summary>
internal static class TestDescendantProcess
{
    /// <summary>
    /// Kills the process tree rooted at <paramref name="processId"/> when it is still running. A tree kill
    /// that could not reach every member (the <see cref="AggregateException"/>
    /// <c>Process.Kill(entireProcessTree: true)</c> documents) never replaces the assertion the calling
    /// <c>finally</c> is running for, but it is reported, because a surviving member is a leaked process.
    /// </summary>
    internal static void KillTreeIfRunning(
        int processId,
        Action<global::System.Diagnostics.Process>? kill = null,
        Action<string>? report = null)
    {
        try
        {
            using global::System.Diagnostics.Process descendant =
                global::System.Diagnostics.Process.GetProcessById(processId);

            (kill ?? KillEntireTree)(descendant);
        }
        catch (AggregateException ex)
        {
            (report ?? TestDiagnostics.Report)(
                $"Process {processId} left part of its tree running after the test's cleanup tried to kill it: "
                + ex.GetBaseException().Message);
        }
        catch (Exception ex) when (IsDescendantAlreadyGone(ex))
        {
            // The descendant already exited, which is the outcome the test wants; nothing is left to kill.
        }
    }

    /// <summary>Kills the process tree whose pid the test's child recorded in <paramref name="pidFile"/>.</summary>
    internal static void KillRecorded(string pidFile)
    {
        try
        {
            KillTreeIfRunning(int.Parse(File.ReadAllText(pidFile), CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            // The descendant may never have written its pid file; either way there is nothing left to
            // kill and the test's own assertions have already run.
        }
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is how a kill reports that there was nothing left to kill: no
    /// such process, or one that exited while it was being opened.
    /// </summary>
    internal static bool IsDescendantAlreadyGone(Exception exception) =>
        exception is IOException
            or FormatException
            or ArgumentException
            or InvalidOperationException
            or Win32Exception;

    private static void KillEntireTree(global::System.Diagnostics.Process descendant) =>
        descendant.Kill(entireProcessTree: true);
}
