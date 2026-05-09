using RetroDownfall.Arcanum.Core.Storage;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Services;

internal static class CliSessionManager
{
    private static string SessionFilePath =>
        Path.Combine(ArcanumPaths.GrimoireDirectory, "cli-session.txt");

    public static Guid? GetLastConversationId()
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
            AnsiConsole.MarkupLine("[dim yellow]Warning: Could not save/load session state.[/]");

            return null;
        }
        catch (UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine("[dim yellow]Warning: Could not save/load session state.[/]");

            return null;
        }
    }

    public static void SaveConversationId(Guid id)
    {
        try
        {
            Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

            File.WriteAllText(SessionFilePath, id.ToString("D"));
        }
        catch (IOException)
        {
            AnsiConsole.MarkupLine("[dim yellow]Warning: Could not save/load session state.[/]");
        }
        catch (UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine("[dim yellow]Warning: Could not save/load session state.[/]");
        }
    }

    public static void ClearSession()
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
            AnsiConsole.MarkupLine("[dim yellow]Warning: Could not save/load session state.[/]");
        }
        catch (UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine("[dim yellow]Warning: Could not save/load session state.[/]");
        }
    }
}
