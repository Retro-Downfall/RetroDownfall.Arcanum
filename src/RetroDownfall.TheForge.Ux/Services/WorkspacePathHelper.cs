namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Separates API workspace query values from Workbench document-identity keys.
/// Identity normalization must never rewrite the path sent as <c>?workspace=</c>.
/// </summary>
public static class WorkspacePathHelper
{

    /// <summary>
    /// Trim and empty-to-null for API query strings. Preserves the caller path otherwise
    /// (no trailing-separator collapse — that would risk diverging from the on-disk workspace).
    /// </summary>
    public static string? ForApi(string? workspace)
    {

        if (string.IsNullOrWhiteSpace(workspace))
        {

            return null;

        }

        return workspace.Trim();

    }

    /// <summary>
    /// Normalize for <c>DocumentKey</c> equality: trim, empty-to-null, then strip trailing
    /// directory separators so <c>/ws</c> and <c>/ws/</c> share one tab.
    /// </summary>
    public static string? ForIdentity(string? workspace)
    {

        string? api = ForApi(workspace);

        if (api is null)
        {

            return null;

        }

        return api.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    }

}
