namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Result of a <c>204 No Content</c>-shaped DELETE. These routes carry no response envelope to unwrap
/// and the body is deliberately never buffered, so the failure reason has to travel out of the client
/// itself. A bare <see cref="bool"/> made every data source invent one — which is how a refused
/// connection, a 403, and a 500 all reached the operator as "not found".
/// </summary>
/// <param name="Success"><see langword="true"/> for any 2xx.</param>
/// <param name="ErrorCode">
/// <c>Http.{status}</c> for a non-2xx response, or the same transport/configuration codes
/// <c>ArcanumApiClient</c> puts on a synthesized failure envelope: <c>Connection.Failed</c>,
/// <c>Connection.Timeout</c>, <c>Security.MissingApiKey</c>, <c>Config.InvalidBaseUrl</c>,
/// <c>Config.InvalidApiKey</c>.
/// </param>
/// <param name="ErrorMessage">Operator-facing detail, or <see langword="null"/> on success.</param>
public sealed record DeleteOutcome(bool Success, string? ErrorCode, string? ErrorMessage)
{

    public static DeleteOutcome Ok() => new(true, null, null);

    public static DeleteOutcome Fail(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage);

}
