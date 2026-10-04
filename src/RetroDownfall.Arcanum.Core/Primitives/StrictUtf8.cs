using System.Text;

namespace RetroDownfall.Arcanum.Core.Primitives;

/// <summary>
/// The one strict UTF-8 encoding used wherever a string is measured or hashed against a grammar
/// that has no representation for an unpaired surrogate. The default <see cref="Encoding.UTF8"/>
/// silently substitutes U+FFFD, so two different invalid strings would measure and hash alike.
/// </summary>
internal static class StrictUtf8
{
    public static UTF8Encoding Encoding { get; } =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Encodes <paramref name="value"/>, returning false rather than throwing for an unpaired surrogate.</summary>
    public static bool TryGetBytes(string value, out byte[] bytes)
    {
        try
        {
            bytes = Encoding.GetBytes(value);

            return true;
        }
        catch (EncoderFallbackException)
        {
            bytes = [];

            return false;
        }
    }
}
