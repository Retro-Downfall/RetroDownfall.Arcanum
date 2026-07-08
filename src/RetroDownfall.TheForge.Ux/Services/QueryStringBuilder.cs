using System.Text;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>Tiny helper for composing <c>?key=value&amp;...</c> query strings from optional parameters.</summary>
internal static class QueryStringBuilder
{

    public static string Build(string path, params (string Key, string? Value)[] parameters)
    {

        StringBuilder builder = new(path);

        bool first = true;

        foreach ((string key, string? value) in parameters)
        {

            if (string.IsNullOrEmpty(value))
            {

                continue;

            }

            builder.Append(first ? '?' : '&');

            first = false;

            builder.Append(key).Append('=').Append(Uri.EscapeDataString(value));

        }

        return builder.ToString();

    }

}
