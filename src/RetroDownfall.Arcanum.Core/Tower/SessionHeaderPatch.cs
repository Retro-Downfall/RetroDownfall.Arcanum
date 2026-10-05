namespace RetroDownfall.Arcanum.Core.Tower;

/// <summary>
/// The header fields of one Session a PATCH actually supplied. A field the caller did not supply is left
/// exactly as stored, however it has changed since the caller last read the Session.
/// </summary>
/// <param name="SetTitle">Whether <paramref name="Title"/> was supplied; a supplied null clears the title.</param>
/// <param name="Title">The new title when <paramref name="SetTitle"/> is true.</param>
/// <param name="Status">The new, already normalized status (<c>active</c> or <c>archived</c>), or null to leave it.</param>
public sealed record SessionHeaderPatch(bool SetTitle, string? Title, string? Status);
