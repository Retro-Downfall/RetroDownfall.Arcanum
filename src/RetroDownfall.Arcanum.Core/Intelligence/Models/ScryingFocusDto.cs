namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

/// <summary>
/// A Scrying focus (image) attached to a <see cref="PingRequest"/> turn — ephemeral, passed
/// through to the provider as a <c>data:</c> URI and never persisted to the Grimoire.
/// </summary>
/// <param name="Data">Base64-encoded image bytes.</param>
/// <param name="MimeType">Image MIME type, e.g. <c>image/png</c>.</param>
public sealed record ScryingFocusDto(string Data, string MimeType);
