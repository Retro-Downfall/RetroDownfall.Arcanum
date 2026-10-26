using RetroDownfall.Arcanum.Core.Primitives;

using System.Globalization;

namespace RetroDownfall.Arcanum.Core.Memory;

/// <summary>Read-only, content-free progress from the installation schema journal.</summary>
public sealed record GrimoireSchemaTransitionStatusDto(bool JournalAvailable, GrimoireSchemaTransitionRunDto[] Runs);

/// <summary>Committed progress, never an estimate of remaining work or a private sweep cursor.</summary>
public sealed record GrimoireSchemaTransitionRunDto(
    string Tier,
    int FromVersion,
    int CompletedThroughVersion,
    int TargetVersion,
    string Sweep,
    long RowsProcessed,
    string? LastErrorCode,
    DateTimeOffset UpdatedAtUtc);

public static class GrimoireSchemaTransitionFacts
{
    public static string Describe(GrimoireSchemaTransitionRunDto run) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{run.Tier}: {run.Sweep}, version {run.FromVersion} through {run.CompletedThroughVersion} toward {run.TargetVersion}; {run.RowsProcessed} committed rows; last update {run.UpdatedAtUtc:O}; last error {run.LastErrorCode ?? "none"}.");

    public static string? SafeErrorCode(string? code) => code switch
    {
        null => null,
        ErrorCodes.Grimoire.Schema.TransitionPassFailed or ErrorCodes.Grimoire.Schema.TransitionUnresumable
            or ErrorCodes.Grimoire.Schema.FinalizationFailed or ErrorCodes.Grimoire.Schema.Busy
            or ErrorCodes.Grimoire.Schema.DiskFull or ErrorCodes.Grimoire.Schema.StorageUnavailable
            or ErrorCodes.Grimoire.Schema.CorruptDatabase or ErrorCodes.Grimoire.Schema.SqliteFailure
            or ErrorCodes.Grimoire.Schema.AccessDenied or ErrorCodes.Grimoire.Schema.InvariantViolation => code,
        _ => ErrorCodes.Grimoire.Schema.UnrecognizedFailure,
    };
}
