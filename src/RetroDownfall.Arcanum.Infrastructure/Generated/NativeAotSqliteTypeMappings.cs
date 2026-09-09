using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using ProviderSqliteDateTimeOffsetTypeMapping = Microsoft.EntityFrameworkCore.Sqlite.Storage.Internal.SqliteDateTimeOffsetTypeMapping;
using ProviderSqliteDateTimeTypeMapping = Microsoft.EntityFrameworkCore.Sqlite.Storage.Internal.SqliteDateTimeTypeMapping;
using ProviderSqliteDecimalTypeMapping = Microsoft.EntityFrameworkCore.Sqlite.Storage.Internal.SqliteDecimalTypeMapping;
using ProviderSqliteGuidTypeMapping = Microsoft.EntityFrameworkCore.Sqlite.Storage.Internal.SqliteGuidTypeMapping;
using ProviderSqliteStringTypeMapping = Microsoft.EntityFrameworkCore.Sqlite.Storage.Internal.SqliteStringTypeMapping;

namespace RetroDownfall.Arcanum.Infrastructure.Generated;

#pragma warning disable EF1001 // Isolated compatibility boundary for EF optimizer-emitted provider mappings.

/// <summary>
/// Native AOT-safe SQLite mappings that intentionally shadow EF 10's provider mapping names in
/// optimizer-generated sources.
/// </summary>
/// <remarks>
/// EF 10's non-generic mappings create their default comparers through <c>MakeGenericMethod</c>,
/// which has no native code in an AOT executable. EF 11 fixes this upstream with generic mappings.
/// These closed mappings apply the same design without editing files recreated by
/// <c>dotnet ef dbcontext optimize --nativeaot</c>.
/// </remarks>
internal static class SqliteGuidTypeMapping
{
    internal static RelationalTypeMapping Default { get; } = NativeAotSqliteTypeMapping.WithClosedComparers<Guid>(
        ProviderSqliteGuidTypeMapping.Default);
}

internal static class SqliteStringTypeMapping
{
    internal static RelationalTypeMapping Default { get; } = NativeAotSqliteTypeMapping.WithClosedComparers<string>(
        ProviderSqliteStringTypeMapping.Default);
}

internal static class SqliteDecimalTypeMapping
{
    internal static RelationalTypeMapping Default { get; } = NativeAotSqliteTypeMapping.WithClosedComparers<decimal>(
        ProviderSqliteDecimalTypeMapping.Default);
}

internal static class SqliteDateTimeTypeMapping
{
    internal static RelationalTypeMapping Default { get; } = NativeAotSqliteTypeMapping.WithClosedComparers<DateTime>(
        ProviderSqliteDateTimeTypeMapping.Default);
}

internal static class SqliteDateTimeOffsetTypeMapping
{
    internal static RelationalTypeMapping Default { get; } = NativeAotSqliteTypeMapping.WithClosedComparers<DateTimeOffset>(
        ProviderSqliteDateTimeOffsetTypeMapping.Default);
}

file static class NativeAotSqliteTypeMapping
{
    internal static RelationalTypeMapping WithClosedComparers<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicMethods
            | DynamicallyAccessedMemberTypes.PublicProperties)]
        T>(RelationalTypeMapping mapping) =>
        mapping.Clone(
            comparer: ValueComparer.CreateDefault<T>(favorStructuralComparisons: false),
            keyComparer: ValueComparer.CreateDefault<T>(favorStructuralComparisons: true),
            providerValueComparer: ValueComparer.CreateDefault<T>(favorStructuralComparisons: false));
}

#pragma warning restore EF1001
