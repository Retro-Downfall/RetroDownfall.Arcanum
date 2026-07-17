namespace RetroDownfall.TheForge.Core.Services;

/// <summary>Outcome of <see cref="ApiKeyResolver.ResolveAsync"/>.</summary>
public readonly record struct ApiKeyResolution(string? Key, bool IsSessionOnly);
