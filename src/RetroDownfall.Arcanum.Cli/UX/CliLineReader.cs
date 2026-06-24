using System.Text;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// Reads a single line with Ctrl+C support (returns <see langword="null"/> when cancelled).
/// </summary>
internal static class CliLineReader
{

    public static string? ReadLine(string promptMarkup, bool allowEmpty)
    {

        AnsiConsole.Markup(promptMarkup);

        StringBuilder sb = new();

        while (true)
        {

            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {

                Console.WriteLine();

                return null;

            }

            if (key.Key == ConsoleKey.Enter)
            {

                Console.WriteLine();

                string line = sb.ToString();

                if (!allowEmpty && string.IsNullOrWhiteSpace(line))
                {

                    continue;

                }

                return line;

            }

            if (key.Key == ConsoleKey.Backspace)
            {

                if (sb.Length > 0)
                {

                    sb.Length -= 1;

                    Console.Write("\b \b");

                }

                continue;

            }

            if (char.IsControl(key.KeyChar))
            {

                continue;

            }

            sb.Append(key.KeyChar);

            Console.Write(key.KeyChar);

        }

    }

}
