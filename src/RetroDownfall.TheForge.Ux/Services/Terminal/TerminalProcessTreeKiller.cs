using System.ComponentModel;
using System.Diagnostics;

namespace RetroDownfall.TheForge.Ux.Services.Terminal;

/// <summary>
/// Best-effort process-tree kill for Hearth Stop/dispose. Swallows expected races where the
/// process already exited. Forge-local copy of Arcanum's internal ProcessTreeKiller pattern.
/// </summary>
internal static class TerminalProcessTreeKiller
{

    public static void TryKillEntireTree(Process process)
    {

        try
        {

            if (!process.HasExited)
            {

                process.Kill(entireProcessTree: true);

            }

        }
        catch (InvalidOperationException)
        {

            // Process already exited.

        }
        catch (Win32Exception)
        {

            // OS-level race while exiting.

        }
        catch (NotSupportedException)
        {

            TryKillSingleProcess(process);

        }
        catch (Exception)
        {

            // Never let teardown throw.

        }

    }

    private static void TryKillSingleProcess(Process process)
    {

        try
        {

            if (!process.HasExited)
            {

                process.Kill();

            }

        }
        catch (InvalidOperationException)
        {

        }
        catch (Win32Exception)
        {

        }
        catch (Exception)
        {

        }

    }

}
