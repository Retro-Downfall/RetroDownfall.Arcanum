namespace RetroDownfall.Arcanum.Infrastructure.Intelligence;

/// <summary>
/// Shared constants for the <c>browse_web</c> built-in tool. Lives in Infrastructure so the named
/// <see cref="HttpClient"/> registration can reference the same name the tool uses in the API project.
/// </summary>
public static class ArcanumBrowseWebConstants
{

    public const string HttpClientName = "ArcanumBrowseWeb";

}
