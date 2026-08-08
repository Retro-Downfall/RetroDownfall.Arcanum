using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

/// <summary>
/// One option as the parser actually declares it. <see cref="Aliases"/> holds every accepted
/// spelling beyond <see cref="Name"/>; after the alias removal there is normally at most one, and
/// it is a short flag.
/// </summary>
internal sealed record CliSurfaceOption(
    string Name,
    IReadOnlyList<string> Aliases,
    string Description,
    bool Recursive,
    bool TakesValue,
    bool Repeatable,
    IReadOnlyList<string> Values,
    string? Completion);

internal sealed record CliSurfaceArgument(
    string Name,
    string Description,
    bool Required,
    bool Variadic,
    IReadOnlyList<string> Values,
    string? Completion);

/// <summary>
/// One command node. <see cref="Path"/> is the canonical space-separated invocation without the
/// executable name, so <c>session show</c> — never an alias spelling, because none survive.
/// </summary>
internal sealed record CliSurfaceCommand(
    string Path,
    string Name,
    string Description,
    bool Runnable,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<CliSurfaceArgument> Arguments,
    IReadOnlyList<CliSurfaceOption> Options,
    IReadOnlyList<string> Examples,
    IReadOnlyList<CliSurfaceCommand> Commands);

/// <summary>
/// The published short-flag contract: one meaning per alias across the entire tree.
/// </summary>
internal sealed record CliSurfaceShortOption(
    string Alias,
    string Option,
    IReadOnlyList<string> Scopes);

internal sealed record CliSurfaceMap(
    IReadOnlyList<CliSurfaceOption> GlobalOptions,
    IReadOnlyList<CliSurfaceShortOption> ShortOptions,
    IReadOnlyList<CliSurfaceCommand> Commands);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    IndentSize = 2)]
[JsonSerializable(typeof(CliSurfaceMap))]
internal sealed partial class CliSurfaceJsonContext : JsonSerializerContext;
