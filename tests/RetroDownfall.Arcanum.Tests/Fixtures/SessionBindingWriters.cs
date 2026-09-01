using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>
/// The two production writers of <c>session_campaign_bindings</c>, driven rather than imitated.
/// </summary>
/// <remarks>
/// There are exactly two, and every installation holds rows from both: the turn-begin repository writes
/// one for each Session created since the binding table shipped, and the core data initializer backfills
/// one for each Session that predates it. They disagreed about how to spell
/// <c>session_campaign_bindings.CampaignId</c> - the initializer canonicalized and the repository bound
/// a bare <c>ToString()</c> - and because that column carries no foreign key nothing reconciled them, so
/// Campaign-scoped recall returned only the half whose binding came from the repository.
///
/// <para>Kept in one place because two suites need both halves, and because a case that seeded the
/// binding itself would be stating the very thing under test. A fixture cannot show that two writers
/// agree.</para>
/// </remarks>
internal static class SessionBindingWriters
{

    /// <summary>
    /// Creates a Session through the turn-begin repository, which writes its binding in the same
    /// transaction.
    /// </summary>
    internal static async Task<Guid> BoundByTheRepositoryAsync(
        ArcanumDbContext db,
        Guid campaignId,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(db);

        GrimoireRepository repository = new(
            db,
            new NoOpSessionAttachmentStore(),
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            attachmentIndex: null,
            covenantKernel: null,
            FixtureOrdinaryConnectionFactory.For(db));

        Result<Guid> created = await repository.CreateBoundSessionAsync(
            CanonicalCampaignContext.Create(
                SessionCampaignBinding.ForCampaign(campaignId),
                campaignAvailabilityGeneration: 1,
                pathIdentityPolicyVersion: 1,
                pathIdentityRevision: null,
                rootIdentityDigest: null),
            "a new conversation",
            cancellationToken);

        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : string.Empty);

        return created.Value;

    }

    /// <summary>
    /// Writes a Session with its legacy navigation Campaign and no binding at all - the state an upgrade
    /// finds - and then runs the shipped core data initializer against it, which is what gives such a
    /// Session its binding on a real installation.
    /// </summary>
    internal static async Task<Guid> BoundByTheInitializerAsync(
        ArcanumDbContext db,
        Guid campaignId,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(db);

        Guid sessionId = Guid.NewGuid();

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync(cancellationToken);

        }

        await using (SqliteCommand command = connection.CreateCommand())
        {

            command.CommandText = """
                INSERT INTO "Sessions" ("Id", "CampaignId", "Status", "CreatedAt", "UpdatedAt")
                VALUES ($id, $campaignId, 'active', $now, $now);
                """;

            _ = command.Parameters.AddWithValue("$id", Canonical(sessionId));

            _ = command.Parameters.AddWithValue("$campaignId", Canonical(campaignId));

            _ = command.Parameters.AddWithValue(
                "$now",
                DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

            _ = await command.ExecuteNonQueryAsync(cancellationToken);

        }

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await new CoreGrimoireSchemaDataInitializer().InitializeAsync(
            connection,
            transaction,
            GrimoireSchemaTestInstaller.CreateContext(),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return sessionId;

    }

    /// <summary>The spelling the object-relational writer renders, which both columns above hold.</summary>
    private static string Canonical(Guid identity) => identity.ToString("D").ToUpperInvariant();

}
