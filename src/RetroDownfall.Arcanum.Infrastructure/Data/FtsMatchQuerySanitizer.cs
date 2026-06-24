using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

public static class FtsMatchQuerySanitizer
{

    private static readonly HashSet<string> ReservedTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND",
        "OR",
        "NOT",
        "NEAR",
    };

    public static string Sanitize(string query)
    {

        StringBuilder builder = new(query.Length);

        bool pendingSpace = false;

        var currentToken = new StringBuilder();

        foreach (char c in query)
        {

            if (char.IsLetterOrDigit(c) || c == '_')
            {

                if (pendingSpace && builder.Length > 0)
                {

                    AppendToken(builder, currentToken);

                    pendingSpace = false;

                }

                pendingSpace = false;

                _ = currentToken.Append(c);

            }
            else if (char.IsWhiteSpace(c))
            {

                if (currentToken.Length > 0)
                {

                    AppendToken(builder, currentToken);

                }

                pendingSpace = builder.Length > 0;

            }
            else
            {

                if (currentToken.Length > 0)
                {

                    AppendToken(builder, currentToken);

                }

                pendingSpace = builder.Length > 0;

            }

        }

        if (currentToken.Length > 0)
        {

            AppendToken(builder, currentToken);

        }

        return builder.ToString().Trim();

    }

    private static void AppendToken(StringBuilder builder, StringBuilder currentToken)
    {

        string token = currentToken.ToString();

        currentToken.Clear();

        if (token.Length == 0)
        {

            return;

        }

        if (builder.Length > 0)
        {

            _ = builder.Append(' ');

        }

        if (ReservedTokens.Contains(token))
        {

            _ = builder.Append('"');

            _ = builder.Append(token);

            _ = builder.Append('"');

            return;

        }

        _ = builder.Append(token);

    }

}

