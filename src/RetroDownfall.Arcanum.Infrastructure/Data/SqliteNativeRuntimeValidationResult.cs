namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// What the native runtime proved about itself before the Grimoire was allowed to open.
/// </summary>
/// <remarks>
/// Deliberately carries no path, key, or database content: this is surfaced through diagnostics and
/// logs. <see cref="ErrorCode" /> is a closed identifier, not a message to be parsed.
/// </remarks>
internal sealed record SqliteNativeRuntimeValidationResult
{

    internal required bool IsValid { get; init; }

    /// <summary>
    /// Closed failure identifier, or <c>null</c> when <see cref="IsValid" /> is <c>true</c>.
    /// </summary>
    internal string? ErrorCode { get; init; }

    internal string? SqliteVersion { get; init; }

    internal string? CipherVersion { get; init; }

    internal string? CipherProvider { get; init; }

    internal string? CipherProviderVersion { get; init; }

    /// <summary>Encrypted create, close, and reopen with the correct key round-tripped.</summary>
    internal bool CodecRoundTripPassed { get; init; }

    /// <summary>Opening with a different key failed on first page access.</summary>
    internal bool WrongKeyRejected { get; init; }

    /// <summary>Every row of <c>PRAGMA cipher_integrity_check</c> reported ok.</summary>
    internal bool CipherIntegrityPassed { get; init; }

    /// <summary>FTS5 exists, honors secure-delete, and passes rank-1 integrity afterwards.</summary>
    internal bool FtsSecureDeletePassed { get; init; }

    /// <summary><c>load_extension()</c> could not be invoked.</summary>
    internal bool LoadExtensionBlocked { get; init; }

    /// <summary>The delivered binary hashed to the value recorded in the manifest.</summary>
    internal bool AssetHashMatched { get; init; }

    /// <summary>
    /// Compile options the manifest requires that the running library did not report.
    /// </summary>
    internal IReadOnlyList<string> MissingCompileOptions { get; init; } = [];

    internal static SqliteNativeRuntimeValidationResult Failure(string errorCode) =>
        new() { IsValid = false, ErrorCode = errorCode };

}
