using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static class RetentionClaimRouteFixture
{
    internal static async Task<Guid> SeedAsync(ArcanumWebApplicationFactory factory, IdempotencyClaimState state)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        await db.Database.OpenConnectionAsync();

        using SqliteCommand command = ((SqliteConnection)db.Database.GetDbConnection()).CreateCommand();

        Guid id = Guid.NewGuid();

        command.CommandText = """
            INSERT INTO IdempotencyClaims
                (Id, ClaimKeyHash, FingerprintHash, State, OwnerId, LeaseExpiresAt, HeartbeatAt,
                 TerminalStreamComplete, CreatedAt, UpdatedAt)
            VALUES (@id, @key, 'retention-route-fingerprint', @state, 'expired-owner', @old, @old, 0, @old, @old)
            """;

        command.Parameters.AddWithValue("@id", id.ToString("N"));

        command.Parameters.AddWithValue("@key", "retention-route-" + id.ToString("N"));

        command.Parameters.AddWithValue("@state", (int)state);

        command.Parameters.AddWithValue("@old", UtcInstantText.Format(DateTimeOffset.UtcNow.AddDays(-2)));

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        return id;
    }

    internal static async Task<int> CleanupExpiredAsync(ArcanumWebApplicationFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        return await new IdempotencyClaimStore(db).DeleteExpiredAsync(DateTimeOffset.UtcNow.AddHours(-24));
    }
}
