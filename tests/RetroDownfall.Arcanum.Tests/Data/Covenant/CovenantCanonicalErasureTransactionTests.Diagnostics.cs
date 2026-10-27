using RetroDownfall.Arcanum.Infrastructure.Backup;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed partial class CovenantCanonicalErasureTransactionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_canonical_storage_refusal_has_safe_cause_and_honest_holder_guidance(bool busy)
    {
        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        var transition = await fixture.PreselectAsync(Token);

        Exception cause = busy
            ? new GrimoireBusyTimeoutException(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), new InvalidOperationException("PRIVATE-path/key/SQL"))
            : new SqliteException("PRIVATE-path/key/SQL", 10);

        CovenantCanonicalErasureTransaction service = new(new RefusingAuthorization(cause), TimeProvider.System);

        var result = await fixture.InClosedPeriodAsync(authority => service.ApplyAsync(CovenantExclusiveOperation.CovenantReset, transition, authority, Token));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, result.Error.Code);

        Assert.DoesNotContain("PRIVATE", result.Error.Message, StringComparison.Ordinal);

        if (busy)
        {
            Assert.Contains("this host", result.Error.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("operation", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains("SQLite=10", result.Error.Message, StringComparison.Ordinal);
        }

        await fixture.ReopenAsync(Token);

        Assert.Equal(1, await fixture.CountAsync("covenant_entries", Token));
    }

    private sealed class RefusingAuthorization(Exception cause) : ICovenantSqliteConnectionInitializer
    {
        public ValueTask InitializeAsync(SqliteConnection connection, CovenantSqliteConnectionMode mode, CancellationToken cancellationToken) =>
            CovenantSqliteConnectionInitializer.Instance.InitializeAsync(connection, mode, cancellationToken);

        public CovenantSqliteAuthorizationScope Authorize(SqliteConnection connection, CovenantSqliteAuthorizationKind kind) => throw cause;

        public CovenantSqliteAuthorizationScope AuthorizeRestoreStagingManagedAuthoritySanitization(
            RestoreStagingManagedAuthoritySanitizationCapability authority,
            RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity) => throw new NotSupportedException();
    }
}
