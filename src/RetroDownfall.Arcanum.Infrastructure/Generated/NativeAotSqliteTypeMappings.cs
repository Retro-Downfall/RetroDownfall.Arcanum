using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Workspaces;
using ProviderIntTypeMapping = Microsoft.EntityFrameworkCore.Storage.IntTypeMapping;
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

internal static class NativeAotSqliteInt32EnumTypeMapping<
    [DynamicallyAccessedMembers(
        DynamicallyAccessedMemberTypes.PublicMethods
        | DynamicallyAccessedMemberTypes.PublicProperties)]
    TEnum>
    where TEnum : struct, Enum
{
    internal static RelationalTypeMapping Default { get; } = Create();

    private static RelationalTypeMapping Create()
    {
        ValueConverter<TEnum, int> converter = new EnumToNumberConverter<TEnum, int>();
        ValueComparer<TEnum> comparer = new(
            static (left, right) => EqualityComparer<TEnum>.Default.Equals(left, right),
            static value => EqualityComparer<TEnum>.Default.GetHashCode(value),
            static value => value);
        ValueComparer<int> providerComparer = new(
            static (left, right) => left == right,
            static value => value,
            static value => value);

        return ProviderIntTypeMapping.Default.Clone(
            comparer: comparer,
            keyComparer: comparer,
            providerValueComparer: providerComparer,
            mappingInfo: new RelationalTypeMappingInfo(storeTypeName: "INTEGER"),
            converter: converter,
            jsonValueReaderWriter: new JsonConvertedValueReaderWriter<TEnum, int>(
                JsonInt32ReaderWriter.Instance,
                converter));
    }
}

internal static class NativeAotSqliteEnumTypeMappings
{
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:System.Enum.GetValues(System.Type)",
        Justification = "EF Core 10 traces its non-generic System.Enum.GetValues(System.Type) query helper through this closed MessageRole mapping. The generated converter and comparer are closed here, and the published Native AOT persistence smoke exercises the mapping.")]
    internal static RelationalTypeMapping ForMessageRole()
        => NativeAotSqliteInt32EnumTypeMapping<MessageRole>.Default;

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:System.Enum.GetValues(System.Type)",
        Justification = "EF Core 10 traces its non-generic System.Enum.GetValues(System.Type) query helper through this closed WorkspaceType mapping. The generated converter and comparer are closed here, and the published Native AOT persistence smoke exercises the mapping.")]
    internal static RelationalTypeMapping ForWorkspaceType()
        => NativeAotSqliteInt32EnumTypeMapping<WorkspaceType>.Default;
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
