namespace RetroDownfall.Arcanum.Core.Desktop;

/// <summary>Shell family used only to render copyable diagnostic commands.</summary>
public enum CommandDisplayPlatform
{

    MacOS = 0,

    Linux = 1,

    Windows = 2,

}

/// <summary>
/// Renders one argument for display without changing the structured arguments used to launch a
/// process. POSIX shells and PowerShell require different apostrophe escaping.
/// </summary>
public static class CommandDisplayFormatter
{

    public static string QuoteArgumentForCurrentPlatform(string value)
    {

        CommandDisplayPlatform platform = OperatingSystem.IsWindows()
            ? CommandDisplayPlatform.Windows
            : OperatingSystem.IsMacOS()
                ? CommandDisplayPlatform.MacOS
                : CommandDisplayPlatform.Linux;

        return QuoteArgument(value, platform);

    }

    public static string QuoteArgument(
        string value,
        CommandDisplayPlatform platform)
    {

        ArgumentNullException.ThrowIfNull(value);

        if (!Enum.IsDefined(platform))
        {

            throw new ArgumentOutOfRangeException(
                nameof(platform),
                platform,
                "Unknown command display platform.");

        }

        if (!RequiresQuoting(value))
        {

            return value;

        }

        return platform == CommandDisplayPlatform.Windows
            ? QuoteForPowerShell(value)
            : QuoteForPosixShell(value);

    }

    private static string QuoteForPowerShell(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string QuoteForPosixShell(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static bool RequiresQuoting(string value)
    {

        if (value.Length == 0 || value[0] == '-')
        {

            return true;

        }

        foreach (char character in value)
        {

            if (IsPortableUnquotedCharacter(character))
            {

                continue;

            }

            return true;

        }

        return false;

    }

    private static bool IsPortableUnquotedCharacter(char character)
    {

        if (character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9')
        {

            return true;

        }

        return character is '_'
            or '%'
            or '+'
            or '='
            or ':'
            or '.'
            or '/'
            or '-';

    }

}
