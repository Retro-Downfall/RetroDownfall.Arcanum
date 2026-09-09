using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Compiled-model mapping that binds a <see cref="DateTimeOffset"/> directly as canonical UTC text.
/// </summary>
internal sealed class UtcDateTimeOffsetTypeMapping : NativeAotUtcInstantTypeMapping<DateTimeOffset>
{
    internal static UtcDateTimeOffsetTypeMapping Default { get; } = new("TEXT");

    private UtcDateTimeOffsetTypeMapping(string storeType)
        : base(storeType)
    {
    }

    private UtcDateTimeOffsetTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new UtcDateTimeOffsetTypeMapping(parameters);

    protected override void ConfigureParameter(DbParameter parameter)
    {
        base.ConfigureParameter(parameter);

        if (parameter.Value is DateTimeOffset value)
        {
            parameter.Value = UtcInstantText.Format(value);
        }
    }

    protected override string GenerateNonNullSqlLiteral(object value) =>
        "'" + UtcInstantText.Format((DateTimeOffset)value) + "'";
}

/// <summary>
/// Compiled-model mapping for historical <see cref="DateTime"/> properties whose unspecified kind
/// means UTC. A Local value is refused rather than changing meaning according to the host time zone.
/// </summary>
internal sealed class UtcDateTimeTypeMapping : NativeAotUtcInstantTypeMapping<DateTime>
{
    internal static UtcDateTimeTypeMapping Default { get; } = new("TEXT");

    private UtcDateTimeTypeMapping(string storeType)
        : base(storeType)
    {
    }

    private UtcDateTimeTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new UtcDateTimeTypeMapping(parameters);

    protected override void ConfigureParameter(DbParameter parameter)
    {
        base.ConfigureParameter(parameter);

        if (parameter.Value is DateTime value)
        {
            parameter.Value = UtcInstantText.Format(value);
        }
    }

    protected override string GenerateNonNullSqlLiteral(object value) =>
        "'" + UtcInstantText.Format((DateTime)value) + "'";
}

/// <summary>
/// EF 10 compatibility boundary that creates UTC mappings with statically closed comparer types.
/// Delete this base and inherit EF 11's generic relational mapping when Arcanum adopts .NET 11.
/// </summary>
internal abstract class NativeAotUtcInstantTypeMapping<
    [DynamicallyAccessedMembers(
        DynamicallyAccessedMemberTypes.PublicMethods
        | DynamicallyAccessedMemberTypes.PublicProperties)]
    T> : RelationalTypeMapping
{
    protected NativeAotUtcInstantTypeMapping(string storeType)
        : base(
            new RelationalTypeMappingParameters(
                new CoreTypeMappingParameters(
                    typeof(T),
                    comparer: ValueComparer.CreateDefault<T>(favorStructuralComparisons: false),
                    keyComparer: ValueComparer.CreateDefault<T>(favorStructuralComparisons: true),
                    providerValueComparer: ValueComparer.CreateDefault<T>(favorStructuralComparisons: false)),
                storeType,
                StoreTypePostfix.None,
                System.Data.DbType.String))
    {
    }

    protected NativeAotUtcInstantTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters)
    {
    }
}
