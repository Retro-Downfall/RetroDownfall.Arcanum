namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// The hermetic SQLCipher library for this runtime identifier could not be loaded.
/// </summary>
/// <remarks>
/// The message names only the runtime identifier and the expected filename. Loader errors carry
/// absolute search paths, environment variables, and installation layout; those belong nowhere near
/// a message an operator may paste into an issue, and there is no recovery an operator could
/// perform with them. The original exception is preserved as the inner exception for local
/// diagnosis.
/// </remarks>
internal sealed class SqliteNativeRuntimeUnavailableException : Exception
{

    internal SqliteNativeRuntimeUnavailableException(
        string runtimeIdentifier,
        string expectedAssetFileName,
        Exception innerException)
        : base(
            $"The hermetic SQLCipher runtime for '{runtimeIdentifier}' could not be loaded. "
            + $"Expected '{expectedAssetFileName}' to be delivered with the application. "
            + "Arcanum does not search for an alternative SQLite library.",
            innerException)
    {

        RuntimeIdentifier = runtimeIdentifier;

        ExpectedAssetFileName = expectedAssetFileName;

    }

    /// <summary>
    /// Stable code for logs and support, so the failure is greppable without parsing the message.
    /// </summary>
    internal string ErrorCode => "Grimoire.NativeRuntimeUnavailable";

    internal string RuntimeIdentifier { get; }

    internal string ExpectedAssetFileName { get; }

}
