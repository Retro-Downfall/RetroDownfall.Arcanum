using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Shared guarded <see cref="Process.Kill(bool)"/> helper for every place Arcanum tears down a spawned
/// child process tree (llama-server via <c>LlamaServerManager</c>, <c>execute_command</c> /
/// <c>run_spell_script</c> via <c>CappedChildProcessRunner</c>, MCP subprocess servers via
/// <c>McpProcessTransport</c>). <see cref="InvalidOperationException"/> and <see cref="Win32Exception"/>
/// are swallowed unconditionally — both are expected races where the process already exited (or the OS
/// process handle was torn down) milliseconds before the kill hook fired, not actionable failures. Falls
/// back to a single-process <see cref="Process.Kill()"/> when the platform does not support
/// <c>entireProcessTree: true</c> (<see cref="NotSupportedException"/>), and never lets an unexpected
/// exception escape — process teardown is always best-effort.
/// </summary>
internal static class ProcessTreeKiller
{

    public static void TryKillEntireTree(Process process, ILogger? logger = null, string? context = null)
    {

        try
        {

            if (!process.HasExited)
            {

                process.Kill(entireProcessTree: true);

            }

        }
        catch (InvalidOperationException ex)
        {

            logger?.LogDebug(ex, "Process kill raced an already-exited process{Context}.", FormatContext(context));

        }
        catch (Win32Exception ex)
        {

            logger?.LogDebug(ex, "Process kill failed at the OS level (process likely already exiting){Context}.", FormatContext(context));

        }
        catch (NotSupportedException)
        {

            TryKillSingleProcess(process, logger, context);

        }
        catch (Exception ex)
        {

            // Never let process teardown throw — this always runs from cleanup/finally/shutdown paths.
            logger?.LogDebug(ex, "Unexpected error killing process tree{Context}.", FormatContext(context));

        }

    }

    private static void TryKillSingleProcess(Process process, ILogger? logger, string? context)
    {

        try
        {

            if (!process.HasExited)
            {

                process.Kill();

            }

        }
        catch (InvalidOperationException ex)
        {

            logger?.LogDebug(ex, "Single-process kill fallback raced an already-exited process{Context}.", FormatContext(context));

        }
        catch (Win32Exception ex)
        {

            logger?.LogDebug(ex, "Single-process kill fallback failed at the OS level{Context}.", FormatContext(context));

        }
        catch (Exception ex)
        {

            logger?.LogDebug(ex, "Unexpected error in single-process kill fallback{Context}.", FormatContext(context));

        }

    }

    private static string FormatContext(string? context) =>
        string.IsNullOrEmpty(context) ? string.Empty : $" ({context})";

}
