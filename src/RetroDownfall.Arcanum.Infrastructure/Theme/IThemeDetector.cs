namespace RetroDownfall.Arcanum.Infrastructure.Theme;

/// <summary>
/// Resolves whether the host OS prefers a dark appearance. Implementations must not throw.
/// </summary>
public interface IThemeDetector
{

    /// <summary>
    /// When <c>true</c>, the effective CLI palette should use the dark semantic set; when <c>false</c>, the light set.
    /// On detection failure, implementations return <c>true</c> (dark fallback).
    /// </summary>
    bool SystemPrefersDark { get; }

}
