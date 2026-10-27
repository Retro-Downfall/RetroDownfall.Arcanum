using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class GrimoireConnectionAdmissionInterceptorTests
{
    [Theory]
    [InlineData(false, 5, 517)]
    [InlineData(true, 5, 517)]
    [InlineData(false, 13, 13)]
    [InlineData(true, 13, 13)]
    [InlineData(false, 26, 26)]
    [InlineData(true, 26, 26)]
    public async Task Connection_diagnostics_preserve_native_codes_without_exception_or_connection_content(
        bool asynchronous, int primary, int extended)
    {
        const string secret = "private-key-and-catalog-path-canary";

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        await using NativeFailureConnection connection = new(new SqliteException(secret, primary, extended));

        DiagnosticCapture capture = new();

        using ILoggerFactory logging = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Warning)
            .AddProvider(capture));

        await using ProbeDbContext context = CreateContext(connection, gate, drain, loggerFactory: logging);

        if (asynchronous)
        {
            await Assert.ThrowsAsync<SqliteException>(() => context.Database.OpenConnectionAsync());
        }
        else
        {
            Assert.Throws<SqliteException>(context.Database.OpenConnection);
        }

        var diagnostic = Assert.Single(capture.Entries);

        Assert.Equal(LogLevel.Error, diagnostic.Level);

        Assert.Null(diagnostic.Exception);

        Assert.Contains(diagnostic.Properties, item => item.Key == "Failure" && Equals(item.Value, GrimoireConnectionFailureKind.Sqlite));

        Assert.Contains(diagnostic.Properties, item => item.Key == "SqliteErrorCode" && Equals(item.Value, primary));

        Assert.Contains(diagnostic.Properties, item => item.Key == "SqliteExtendedErrorCode" && Equals(item.Value, extended));

        Assert.DoesNotContain(secret, diagnostic.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(ConnectionString, diagnostic.Message, StringComparison.Ordinal);

        Assert.All(diagnostic.Properties, item => Assert.DoesNotContain(secret, item.Value?.ToString() ?? "", StringComparison.Ordinal));
    }

    private sealed class NativeFailureConnection(SqliteException failure) : SqliteConnection(GrimoireConnectionAdmissionInterceptorTests.ConnectionString)
    {
        public override void Open() => throw failure;

        public override Task OpenAsync(CancellationToken cancellationToken) => Task.FromException(failure);
    }
}
