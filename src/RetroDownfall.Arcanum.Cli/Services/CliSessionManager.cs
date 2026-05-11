using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Storage;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Services;

public sealed class CliSessionManager(IThemePalette palette)
{

    private int _corruptionWarned;

    private string SessionFilePath =>
        Path.Combine(ArcanumPaths.GrimoireDirectory, "cli-session.txt");

    public Guid? GetLastConversationId()
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

            WarnOnceSessionCorruption(text);

            return null;
        }
        catch (IOException)
        {
            WarnSessionIo();

            return null;
        }
        catch (UnauthorizedAccessException)
        {
            WarnSessionIo();

            return null;
        }
    }

    public void SaveConversationId(Guid id)
    {
        try
        {
            Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

            string finalPath = SessionFilePath;

            // Write to a sibling temp file and atomically replace to avoid partial-write
            // corruption on crash/power-loss mid-write.
            string tempPath = finalPath + ".tmp." + Guid.NewGuid().ToString("N");

            File.WriteAllText(tempPath, id.ToString("D"));

            try
            {
                File.Move(tempPath, finalPath, overwrite: true);
            }
            catch
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
        catch (IOException)
        {
            WarnSessionIo();
        }
        catch (UnauthorizedAccessException)
        {
            WarnSessionIo();
        }
    }

    public void ClearSession()
    {
        try
        {
            if (File.Exists(SessionFilePath))
            {
                File.Delete(SessionFilePath);
            }
        }
        catch (IOException)
        {
            WarnSessionIo();
        }
        catch (UnauthorizedAccessException)
        {
            WarnSessionIo();
        }
    }

    private void WarnSessionIo() =>
        AnsiConsole.MarkupLine(palette.MutedMarkup(Markup.Escape("Warning: Could not save/load session state.")));

    private void WarnOnceSessionCorruption(string actual)
    {
        if (Interlocked.Exchange(ref _corruptionWarned, 1) != 0)
        {
            return;
        }

        string preview = actual.Length > 40 ? actual[..40] + "\u2026" : actual;

        AnsiConsole.MarkupLine(
            palette.ErrorLabelMarkup(
                Markup.Escape("Warning:"),
                Markup.Escape(
                    $"cli-session.txt does not contain a valid conversation id (got: '{preview}'). The file will be replaced on the next /resume or new turn.")));
    }

}
