using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Services;

public sealed class CliSessionManager(IThemePalette palette, ILogger<CliSessionManager>? logger = null)
{
    private int _corruptionWarned;

    private string SessionFilePath =>
        Path.Combine(ArcanumPaths.GrimoireDirectory, "cli-session.txt");

    public Guid? GetLastSessionId(bool quiet = false)
    {
        try
        {
            if (!File.Exists(SessionFilePath))
            {
                return null;
            }

            string text = File.ReadAllText(SessionFilePath).Trim();

            if (text.Length == 0)
            {
                return null;
            }

            if (Guid.TryParse(text, out Guid id))
            {
                return id;
            }

            WarnOnceSessionCorruption(quiet);

            return null;
        }
        catch (IOException ex)
        {
            WarnSessionIo(ex, quiet);

            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            WarnSessionIo(ex, quiet);

            return null;
        }
    }

    public void SaveSessionId(Guid id, bool quiet = false)
    {
        try
        {
            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(ArcanumPaths.GrimoireDirectory);

            string finalPath = SessionFilePath;

            // Write to a sibling temp file and atomically replace to avoid partial-write
            // corruption on crash/power-loss mid-write.
            string tempPath = finalPath + ".tmp." + Guid.NewGuid().ToString("N");

            File.WriteAllText(tempPath, id.ToString("D"));

            SecureFilePermissions.ApplyOwnerOnlyFile(tempPath);

            try
            {
                File.Move(tempPath, finalPath, overwrite: true);

                SecureFilePermissions.ApplyOwnerOnlyFile(finalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of the temp file if Move fails.
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                throw;
            }
        }
        catch (IOException ex)
        {
            WarnSessionIo(ex, quiet);
        }
        catch (UnauthorizedAccessException ex)
        {
            WarnSessionIo(ex, quiet);
        }
    }

    public void ClearSession(bool quiet = false)
    {
        try
        {
            if (File.Exists(SessionFilePath))
            {
                File.Delete(SessionFilePath);
            }
        }
        catch (IOException ex)
        {
            WarnSessionIo(ex, quiet);
        }
        catch (UnauthorizedAccessException ex)
        {
            WarnSessionIo(ex, quiet);
        }
    }

    private void WarnSessionIo(Exception ex, bool quiet)
    {
        if (quiet)
        {
            logger?.LogDebug(
                "Could not save/load CLI session state (quiet); exception type {ExceptionType}.",
                ex.GetType().FullName);
            return;
        }

        AnsiConsole.MarkupLine(palette.MutedMarkup(Markup.Escape("Warning: Could not save/load session state.")));
    }

    private void WarnOnceSessionCorruption(bool quiet)
    {
        if (Interlocked.Exchange(ref _corruptionWarned, 1) != 0)
        {
            return;
        }

        if (quiet)
        {
            logger?.LogDebug(
                "cli-session.txt does not contain a valid session id. Quiet path; no Spectre output.");
            return;
        }

        AnsiConsole.MarkupLine(
            palette.ErrorLabelMarkup(
                Markup.Escape("Warning:"),
                Markup.Escape(
                    "cli-session.txt does not contain a valid session id. The file will be replaced on the next /resume or new turn.")));
    }
}
