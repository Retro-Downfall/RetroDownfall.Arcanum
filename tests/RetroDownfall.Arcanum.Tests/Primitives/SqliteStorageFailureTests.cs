using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Primitives;

public sealed class SqliteStorageFailureTests
{
    [Theory]
    [InlineData(5, SqliteStorageFailureKind.Busy)]
    [InlineData(6, SqliteStorageFailureKind.Busy)]
    [InlineData(261, SqliteStorageFailureKind.Busy)]
    [InlineData(7, SqliteStorageFailureKind.Unavailable)]
    [InlineData(8, SqliteStorageFailureKind.Unavailable)]
    [InlineData(9, SqliteStorageFailureKind.Unavailable)]
    [InlineData(10, SqliteStorageFailureKind.Unavailable)]
    [InlineData(266, SqliteStorageFailureKind.Unavailable)]
    [InlineData(13, SqliteStorageFailureKind.Unavailable)]
    [InlineData(14, SqliteStorageFailureKind.Unavailable)]
    [InlineData(1, SqliteStorageFailureKind.Fault)]
    [InlineData(11, SqliteStorageFailureKind.Fault)]
    [InlineData(19, SqliteStorageFailureKind.Fault)]
    [InlineData(26, SqliteStorageFailureKind.Fault)]
    [InlineData(null, SqliteStorageFailureKind.Fault)]
    public void Classification_preserves_primary_meaning_without_driver_text(int? code, SqliteStorageFailureKind expected) =>
        Assert.Equal(expected, SqliteStorageFailure.Classify(code));
}
