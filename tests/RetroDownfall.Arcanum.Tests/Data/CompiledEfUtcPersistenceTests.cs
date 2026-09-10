using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
public sealed class CompiledEfUtcPersistenceTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private string _databasePath = string.Empty;

    private ArcanumDbContext? _db;

    public Task InitializeAsync()
    {
        _databasePath = fixture.CopyDatabase();

        _db = fixture.CreateContext(_databasePath);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [SkippableFact]
    public void Every_compiled_EF_instant_property_uses_the_canonical_UTC_mapping()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ArcanumDbContext db = _db!;

        (Type Entity, string Property, Type Mapping)[] expected =
        [
            (typeof(Campaign), nameof(Campaign.CreatedAt), typeof(UtcDateTimeOffsetTypeMapping)),
            (typeof(Campaign), nameof(Campaign.UpdatedAt), typeof(UtcDateTimeOffsetTypeMapping)),
            (typeof(Session), nameof(Session.CreatedAt), typeof(UtcDateTimeOffsetTypeMapping)),
            (typeof(Session), nameof(Session.UpdatedAt), typeof(UtcDateTimeOffsetTypeMapping)),
            (typeof(Session), nameof(Session.LastSummarizedMessageAt), typeof(UtcDateTimeTypeMapping)),
            (typeof(Entry), nameof(Entry.CreatedAt), typeof(UtcDateTimeOffsetTypeMapping)),
            (typeof(Prompt), nameof(Prompt.CreatedAt), typeof(UtcDateTimeOffsetTypeMapping)),
            (typeof(Prompt), nameof(Prompt.UpdatedAt), typeof(UtcDateTimeOffsetTypeMapping)),
            (typeof(Apprentice), nameof(Apprentice.CreatedAt), typeof(UtcDateTimeOffsetTypeMapping)),
            (typeof(Apprentice), nameof(Apprentice.UpdatedAt), typeof(UtcDateTimeOffsetTypeMapping)),
            (typeof(WorkspaceContext), nameof(WorkspaceContext.CreatedAt), typeof(UtcDateTimeOffsetTypeMapping)),
            (typeof(MageSetting), nameof(MageSetting.UpdatedAt), typeof(UtcDateTimeTypeMapping)),
        ];

        foreach ((Type entity, string propertyName, Type mapping) in expected)
        {
            IProperty property = db.Model.FindEntityType(entity)!.FindProperty(propertyName)!;

            Assert.IsType(mapping, property.GetTypeMapping());
        }
    }

    [Fact]
    public void EF_optimizer_regeneration_keeps_UTC_mappings_in_the_supported_partial_hooks()
    {
        string[] entityTypes =
        [
            "Campaign",
            "Session",
            "Entry",
            "Prompt",
            "Apprentice",
            "WorkspaceContext",
            "MageSetting",
        ];

        IReadOnlyList<ProductionSource> sources = ProductionSourceInventory.Sources();

        ProductionSource customizations = sources.Single(
            static source => source.Is("UtcInstantEntityTypeCustomizations.cs"));

        foreach (string entityType in entityTypes)
        {
            ProductionSource generated = sources.Single(
                source => source.RelativePath.EndsWith(
                    $"/Generated/{entityType}EntityType.cs",
                    StringComparison.Ordinal));

            Assert.Contains(
                "static partial void Customize(RuntimeEntityType runtimeEntityType)",
                generated.Text,
                StringComparison.Ordinal);

            Assert.DoesNotContain("UtcDateTime", generated.Text, StringComparison.Ordinal);

            Assert.Contains(
                $"public partial class {entityType}EntityType",
                customizations.Text,
                StringComparison.Ordinal);
        }

        Assert.Equal(
            12,
            customizations.Text.Split(".TypeMapping = UtcDateTime", StringSplitOptions.None).Length - 1);
    }

    [SkippableFact]
    public async Task Compiled_EF_writes_offset_and_unspecified_instants_as_fixed_UTC_text()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ArcanumDbContext db = _db!;

        Session session = new()
        {
            Id = Guid.NewGuid(),
            Status = "active",
            CreatedAt = new DateTimeOffset(2026, 9, 8, 23, 0, 0, TimeSpan.FromHours(-4)).AddTicks(7),
            UpdatedAt = new DateTimeOffset(2026, 9, 9, 5, 0, 0, TimeSpan.FromHours(2)).AddTicks(8),
            LastSummarizedMessageAt = new DateTime(2026, 9, 9, 3, 0, 0, DateTimeKind.Unspecified).AddTicks(9),
        };

        _ = db.Sessions.Add(session);

        _ = await db.SaveChangesAsync();

        await using SqliteCommand read = (SqliteCommand)db.Database.GetDbConnection().CreateCommand();

        read.CommandText =
            "SELECT CreatedAt, UpdatedAt, LastSummarizedMessageAt FROM Sessions WHERE Id = $id;";

        _ = read.Parameters.AddWithValue("$id", session.Id);

        await using SqliteDataReader reader = await read.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        Assert.Equal("2026-09-09T03:00:00.0000007Z", reader.GetString(0));

        Assert.Equal("2026-09-09T03:00:00.0000008Z", reader.GetString(1));

        Assert.Equal("2026-09-09T03:00:00.0000009Z", reader.GetString(2));
    }
}
