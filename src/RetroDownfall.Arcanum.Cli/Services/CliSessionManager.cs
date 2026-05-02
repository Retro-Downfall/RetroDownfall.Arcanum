using RetroDownfall.Arcanum.Core.Storage;

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
            return null;
        }
        catch (UnauthorizedAccessException)
        {
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
        }
        catch (UnauthorizedAccessException)
        {
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
