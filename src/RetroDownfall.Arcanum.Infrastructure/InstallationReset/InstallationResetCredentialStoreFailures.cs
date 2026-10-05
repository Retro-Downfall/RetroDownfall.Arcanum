namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

/// <summary>
/// The one filter every credential-store call made for the installation-reset record shares.
/// </summary>
/// <remarks>
/// An ordinary backend failure, or a native backend that is missing or cannot be loaded, is a
/// content-free unavailable result rather than an escaping exception. The key provider and the anchor
/// store each carried a filter of their own, and the anchor store's was the narrower one, so a missing
/// native library surfaced as an exception from the very call the key provider had already turned into
/// a refusal. One definition means there is no second set to drift out of step.
/// </remarks>
internal static class InstallationResetCredentialStoreFailures
{
    internal static bool IsFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or System.Runtime.InteropServices.MarshalDirectiveException
            or TypeLoadException;
}
