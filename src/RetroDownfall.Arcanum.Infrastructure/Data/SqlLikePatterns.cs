using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

public static class SqlLikePatterns
{

    public const char EscapeChar = '\\';

    public const string EscapeString = "\\";

    public static string EscapeLiteral(string literal)
    {
        if (string.IsNullOrEmpty(literal))
        {
            return literal;
        }

        StringBuilder builder = new(literal.Length * 2);

        foreach (char c in literal)
        {
            if (c is '%' or '_' or '\\')
            {
                _ = builder.Append('\\');
            }

            _ = builder.Append(c);
        }

        return builder.ToString();
    }

    public static string Contains(string literal) =>
        $"%{EscapeLiteral(literal)}%";

}
