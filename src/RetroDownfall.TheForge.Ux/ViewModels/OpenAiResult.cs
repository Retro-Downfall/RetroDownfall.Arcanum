namespace RetroDownfall.TheForge.Ux.ViewModels;

/// <summary>
/// Result of an OpenAI-shaped <c>/v1/*</c> call. Carries the bare success payload or the OpenAI
/// error envelope fields (<c>error.message</c> / <c>error.code</c>) — never an
/// <c>ApiResponse&lt;T&gt;</c> envelope.
/// </summary>
public sealed record OpenAiResult<T>(T? Data, bool Success, string? ErrorCode, string? ErrorMessage)
{

    public static OpenAiResult<T> Ok(T data) => new(data, true, null, null);

    public static OpenAiResult<T> Fail(string? code, string message) =>
        new(default, false, code, message);

}
