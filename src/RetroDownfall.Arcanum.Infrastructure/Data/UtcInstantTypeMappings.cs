using System.Data;
using System.Data.Common;

using Microsoft.EntityFrameworkCore.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Compiled-model mapping that binds a <see cref="DateTimeOffset"/> directly as canonical UTC text.
/// </summary>
internal sealed class UtcDateTimeOffsetTypeMapping : RelationalTypeMapping
{
    internal static UtcDateTimeOffsetTypeMapping Default { get; } = new("TEXT");

    private UtcDateTimeOffsetTypeMapping(string storeType)
        : base(storeType, typeof(DateTimeOffset), System.Data.DbType.String)
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
internal sealed class UtcDateTimeTypeMapping : RelationalTypeMapping
{
    internal static UtcDateTimeTypeMapping Default { get; } = new("TEXT");

    private UtcDateTimeTypeMapping(string storeType)
        : base(storeType, typeof(DateTime), System.Data.DbType.String)
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
