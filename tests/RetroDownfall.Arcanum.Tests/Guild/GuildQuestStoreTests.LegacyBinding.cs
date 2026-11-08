using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guild_legacy_binding_query_joins_but_never_disposes_or_commits_its_callers_transaction(bool unavailable)
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext context = GrimoireFixture.CreateContext(connection);

        IApprenticeRepository repository = new ApprenticeRepository(context, NullLogger<ApprenticeRepository>.Instance);

        await using (IDbContextTransaction caller = await context.Database.BeginTransactionAsync())
        {
            await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(context,
                "UPDATE grimoire_feature_schemas SET HealthCode=$health WHERE FamilyCode=2", default);

            command.Parameters.AddWithValue("$health", unavailable ? 1 : 0);

            await command.ExecuteNonQueryAsync();

            Result<bool> result = await repository.IsGuildBoundAsync(Guid.NewGuid());

            Assert.Equal(!unavailable, result.IsSuccess);

            Assert.Same(caller, context.Database.CurrentTransaction);

            command.CommandText = "SELECT HealthCode FROM grimoire_feature_schemas WHERE FamilyCode=2";

            Assert.Equal(unavailable ? 1L : 0L, await command.ExecuteScalarAsync());

            await caller.RollbackAsync();
        }

        Result<bool> afterRollback = await repository.IsGuildBoundAsync(Guid.NewGuid());

        Assert.True(afterRollback.IsSuccess, afterRollback.Error.Message);

        Assert.False(afterRollback.Value);

        Assert.Null(context.Database.CurrentTransaction);
    }

    [Fact]
    public async Task Guild_legacy_binding_query_distinguishes_bound_and_unbound_owners()
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.UseLocalAdmission(new LocalAdmission());

        var issued = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT ApprenticeId FROM guild_local_executions";

        Guid bound = Guid.Parse((string)(await command.ExecuteScalarAsync())!);

        await using ArcanumDbContext context = GrimoireFixture.CreateContext(connection);

        IApprenticeRepository repository = new ApprenticeRepository(context, NullLogger<ApprenticeRepository>.Instance);

        Result<bool> association = await repository.IsGuildBoundAsync(bound);

        Assert.True(association.IsSuccess, association.Error.Message);

        Assert.True(association.Value);

        Result<bool> unrelated = await repository.IsGuildBoundAsync(Guid.NewGuid());

        Assert.True(unrelated.IsSuccess, unrelated.Error.Message);

        Assert.False(unrelated.Value);
    }

    [Theory]
    [InlineData("health")]
    [InlineData("missing-table")]
    [InlineData("missing-registration")]
    [InlineData("extra-registration")]
    public async Task Guild_legacy_binding_query_refuses_uncertain_installed_capability(string failure)
    {
        await using Fixture f = await Fixture.CreateAsync();

        await f.ExecuteAsync(failure switch
        {
            "health" => "UPDATE grimoire_feature_schemas SET HealthCode=1 WHERE FamilyCode=2",
            "missing-table" => "DROP TABLE guild_local_executions",
            "extra-registration" => "INSERT INTO grimoire_feature_schemas SELECT FamilyCode,99,SchemaVersion,SourceDefinitionFingerprint,InstalledCatalogFingerprint,InstalledAtUtc,HealthCode,HealthDetailCode FROM grimoire_feature_schemas WHERE FamilyCode=2",
            _ => "DELETE FROM grimoire_feature_schemas WHERE FamilyCode=2",
        });

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext context = GrimoireFixture.CreateContext(connection);

        IApprenticeRepository repository = new ApprenticeRepository(context, NullLogger<ApprenticeRepository>.Instance);

        Assert.True((await repository.IsGuildBoundAsync(Guid.NewGuid())).IsFailure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("GuIlD_unrecognized_binding")]
    [InlineData("iDx_GuIlD_unrecognized_binding")]
    [InlineData("uX_GuIlD_unrecognized_binding")]
    public async Task Guild_legacy_binding_query_accepts_only_truly_absent_optional_capability(string? residue)
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        foreach (var entry in GrimoireSchemaManifests.GuildCanonical.Entries.OrderBy(entry => entry.Type == GrimoireSchemaObjectType.Table ? 1 : 0))
        {
            string kind = entry.Type switch
            {
                GrimoireSchemaObjectType.Trigger => "TRIGGER",
                GrimoireSchemaObjectType.View => "VIEW",
                _ => "TABLE",
            };

            command.CommandText = $"DROP {kind} IF EXISTS [{entry.Name}]";

            await command.ExecuteNonQueryAsync();
        }

        command.CommandText = "DELETE FROM grimoire_feature_schemas WHERE FamilyCode=2";

        await command.ExecuteNonQueryAsync();

        if (residue is not null)
        {
            command.CommandText = $"CREATE TABLE [{residue}](Id TEXT)";

            await command.ExecuteNonQueryAsync();
        }

        await using ArcanumDbContext context = GrimoireFixture.CreateContext(connection);

        IApprenticeRepository repository = new ApprenticeRepository(context, NullLogger<ApprenticeRepository>.Instance);

        Result<bool> result = await repository.IsGuildBoundAsync(Guid.NewGuid());

        Assert.Equal(residue is null, result.IsSuccess);

        if (residue is null) Assert.False(result.Value);
    }
}
