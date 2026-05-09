using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Storage;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Services;

public sealed class CliSessionManager(IThemePalette palette)
{

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

            return Guid.TryParse(text, out Guid id) ? id : null;
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

            File.WriteAllText(SessionFilePath, id.ToString("D"));
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

}
