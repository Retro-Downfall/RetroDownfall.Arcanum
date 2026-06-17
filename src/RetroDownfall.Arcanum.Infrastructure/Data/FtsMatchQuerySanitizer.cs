using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

public static class FtsMatchQuerySanitizer
{

    public static string Sanitize(string query)
    {
        StringBuilder builder = new(query.Length);

        bool pendingSpace = false;

        foreach (char c in query)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                if (pendingSpace && builder.Length > 0)
                {
                    _ = builder.Append(' ');
                }

                pendingSpace = false;

                _ = builder.Append(c);
            }
            else if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
            }
            else
            {
                pendingSpace = builder.Length > 0;
            }
        }

        return builder.ToString().Trim();
    }

}
