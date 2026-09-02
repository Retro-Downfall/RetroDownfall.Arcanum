using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

internal static class GrimoireOfflineTransitionLeafName
{

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static bool IsValid(string? leaf)
    {

        if (string.IsNullOrEmpty(leaf)
            || leaf is "." or ".."
            || leaf.Contains('/')
            || leaf.Contains('\\'))
        {

            return false;

        }

        try
        {

            return StrictUtf8.GetByteCount(leaf) <= 255;

        }
        catch (EncoderFallbackException)
        {

            return false;

        }

    }

}
