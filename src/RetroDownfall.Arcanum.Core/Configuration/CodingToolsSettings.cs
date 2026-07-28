using System.Collections.Frozen;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Bounded configuration for the in-process repository editing tools.
/// </summary>
public sealed record CodingToolsSettings
{
    public WorkspaceSearchSettings Search { get; set; } = new();

    public WorkspacePatchSettings Patch { get; set; } = new();

    public WorkspaceCheckSettings WorkspaceCheck { get; set; } = new();
}

/// <summary>
/// Resource limits for exact line-scoped workspace search.
/// </summary>
public sealed record WorkspaceSearchSettings
{
    public int MaxPatternChars { get; set; } = 4_096;

    public int RegexTimeoutMilliseconds { get; set; } = 250;

    public int MaxElapsedMilliseconds { get; set; } = 10_000;

    public int MaxFiles { get; set; } = 2_000;

    public long MaxBytes { get; set; } = 32L * 1024L * 1024L;

    public int MaxTraversalSteps { get; set; } = 100_000;

    public int MaxMatches { get; set; } = 1_000;

    public int MaxPreviewChars { get; set; } = 512;
}

/// <summary>
/// Resource and recovery limits for one unified-diff invocation.
/// </summary>
public sealed record WorkspacePatchSettings
{
    public long MaxPatchBytes { get; set; } = 4L * 1024L * 1024L;

    public long MaxInputBytesPerFile { get; set; } = 16L * 1024L * 1024L;

    public long MaxTotalInputBytes { get; set; } = 64L * 1024L * 1024L;

    public long MaxOutputBytesPerFile { get; set; } = 16L * 1024L * 1024L;

    public long MaxTotalOutputBytes { get; set; } = 64L * 1024L * 1024L;

    public long MaxStagingBytesPerFile { get; set; } = 32L * 1024L * 1024L;

    public long MaxTotalStagingBytes { get; set; } = 128L * 1024L * 1024L;

    public int MaxElapsedMilliseconds { get; set; } = 30_000;

    public int RollbackReserveMilliseconds { get; set; } = 5_000;

    public int MaxFiles { get; set; } = 128;

    public int MaxHunks { get; set; } = 1_024;

    public int MaxLinesPerHunk { get; set; } = 10_000;

    public int FuzzyMatchWindowLines { get; set; } = 100;

    public int MaxResultItems { get; set; } = 256;
}

/// <summary>
/// Global workspace-check configuration. Built-in profiles are code-owned; operators may add
/// profiles only by selecting a closed executable catalog entry and exact argument renderings.
/// </summary>
public sealed record WorkspaceCheckSettings
{
    public bool Enabled { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 300;

    public int MaxCustomProfiles { get; set; } = 32;

    public int MaxFixedArgumentsPerProfile { get; set; } = 32;

    public int MaxArgumentTokenChars { get; set; } = 256;

    public int MaxOptionsPerProfile { get; set; } = 16;

    public int MaxAllowedValuesPerOption { get; set; } = 16;

    public int MaxDiagnostics { get; set; } = 500;

    public long MaxOutputBytes { get; set; } = 1L * 1024L * 1024L;

    public WorkspaceCheckExecutableCatalogSettings ExecutableCatalog { get; set; } = new();

    /// <summary>
    /// Opaque operator-authored profile collection. Compendium preserves this dictionary but does
    /// not expose child-field editing for its security-sensitive internals.
    /// </summary>
    public Dictionary<string, WorkspaceCheckProfileSettings> CustomProfiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Closed executable catalog. The first release contains only the trusted native
/// <c>dotnet</c> host; an empty path delegates canonical resolution to the runtime policy.
/// </summary>
public sealed record WorkspaceCheckExecutableCatalogSettings
{
    public WorkspaceCheckExecutableSettings DotNet { get; set; } = new();
}

public sealed record WorkspaceCheckExecutableSettings
{
    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// An operator-defined profile. The model selects the profile and an allowlisted option value;
/// it never supplies executable names, raw argument tokens, templates, or commands.
/// </summary>
public sealed record WorkspaceCheckProfileSettings
{
    public string ExecutableId { get; set; } = WorkspaceCheckCatalogDefaults.DotNetExecutableId;

    public WorkspaceCheckKind Kind { get; set; } = WorkspaceCheckKind.Build;

    public WorkspaceCheckDiagnosticParserKind Parser { get; set; } =
        WorkspaceCheckDiagnosticParserKind.MsBuild;

    public string Target { get; set; } = string.Empty;

    public string[] FixedArguments { get; set; } = [];

    public Dictionary<string, WorkspaceCheckProfileOptionSettings> Options { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Exact, operator-owned argument token renderings keyed by the bounded value exposed to a model.
/// </summary>
public sealed record WorkspaceCheckProfileOptionSettings
{
    public Dictionary<string, string[]> AllowedValues { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<WorkspaceCheckKind>))]
public enum WorkspaceCheckKind
{
    [JsonStringEnumMemberName("build")]
    Build,

    [JsonStringEnumMemberName("test")]
    Test,

    [JsonStringEnumMemberName("lint")]
    Lint,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<WorkspaceCheckDiagnosticParserKind>))]
public enum WorkspaceCheckDiagnosticParserKind
{
    [JsonStringEnumMemberName("msBuild")]
    MsBuild,

    [JsonStringEnumMemberName("vsTest")]
    VsTest,

    [JsonStringEnumMemberName("dotNetFormat")]
    DotNetFormat,
}

/// <summary>
/// Stable IDs shared by configuration validation and the later runtime catalog implementation.
/// </summary>
public static class WorkspaceCheckCatalogDefaults
{
    public const string DotNetExecutableId = "dotnet";

    public const string DotNetBuildProfileId = "dotnet-build";

    public const string DotNetTestProfileId = "dotnet-test";

    public const string DotNetLintProfileId = "dotnet-lint";

    public static IReadOnlySet<string> ReservedProfileIds { get; } =
        new[]
        {
            DotNetBuildProfileId,
            DotNetTestProfileId,
            DotNetLintProfileId,
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
