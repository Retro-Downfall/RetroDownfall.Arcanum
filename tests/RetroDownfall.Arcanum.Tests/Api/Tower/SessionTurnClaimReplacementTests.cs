using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

[Collection("ApiHost")]
public sealed class SessionTurnClaimReplacementTests
{
    [SkippableFact]
    public async Task The_replacement_host_owns_the_admitted_database_when_SQLCipher_normalizes_its_path()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using RestartableArcanumProfileFixture profile = new();

        InMemoryOsCredentialStore credentials = new();

        SessionTurnClaimLease previous;

        await using (var host = MemoryErasureRouteDriver.Host(credentials, profile))
        {
            using HttpClient client = host.CreateAuthenticatedClient();

            using IServiceScope scope = host.Services.CreateScope();

            IServiceProvider services = scope.ServiceProvider;

            Guid installation = Guid.Parse(services.GetRequiredService<ICovenantAuthoritySnapshotProvider>().Current!.InstallationIdentity);

            var created = await services.GetRequiredService<ISessionTurnBeginStore>()
                .CreateBoundSessionAsync(CanonicalCampaignContext.GlobalOnly, "previous boot", default);

            Assert.True(created.IsSuccess, created.Error.Message);

            previous = await AcquireAsync(services, installation, created.Value);

            var begun = await services.GetRequiredService<ISessionTurnClaimBeginStore>()
                .BeginClaimedAssistantReplyAsync(previous, CanonicalCampaignContext.GlobalOnly, "previous boot", "model", default);

            Assert.True(begun.IsSuccess, begun.Error.Message);
        }

        await using var replacement = MemoryErasureRouteDriver.Host(credentials, profile);

        using HttpClient resumed = replacement.CreateAuthenticatedClient();

        using IServiceScope replacementScope = replacement.Services.CreateScope();

        IServiceProvider current = replacementScope.ServiceProvider;

        SqliteConnection admitted = await current.GetRequiredService<ICovenantConnectionSource>().GetOpenCoreConnectionAsync(default);

        string declared = new SqliteConnectionStringBuilder(admitted.ConnectionString).DataSource;

        if (OperatingSystem.IsMacOS() && (declared.StartsWith("/var/", StringComparison.Ordinal) || declared.StartsWith("/tmp/", StringComparison.Ordinal)))
            Assert.Equal("/private" + declared, admitted.DataSource);

        GrimoireDatabaseHostedService owner = current.GetRequiredService<GrimoireDatabaseHostedService>();

        using SqliteConnection unopened = new(admitted.ConnectionString);

        Assert.False(owner.OwnsStartedDatabase(unopened));

        GrimoireFixture staged = profile.Grimoire!;

        await using var otherDatabase = staged.CreateContext(staged.CopyDatabase());

        Assert.False(owner.OwnsStartedDatabase((SqliteConnection)otherDatabase.Database.GetDbConnection()));

        SessionTurnClaimLease next = await AcquireAsync(current, previous.Claim.OriginInstallationId, previous.Claim.SessionId);

        Assert.NotEqual(previous.OwnerBootId, next.OwnerBootId);

        Assert.Equal(SessionTurnClaimDisposition.Created, next.Disposition);

        var settled = await current.GetRequiredService<ISessionTurnClaimLookup>()
            .FindAsync(previous.Claim.OriginInstallationId, previous.Claim.ClientTurnId, default);

        Assert.Equal(SessionTurnClaimState.RestoredInterrupted, settled.Value!.State);
    }

    private static async Task<SessionTurnClaimLease> AcquireAsync(IServiceProvider services, Guid installation, Guid session)
    {
        var input = (await services.GetRequiredService<ISessionTurnClaimBeginStore>().ReadClaimInputAsync(session, default)).Value;

        var acquired = await services.GetRequiredService<ISessionTurnClaimCoordinator>().AcquireAsync(new(installation, 0,
            Guid.NewGuid(), session, SessionTurnSurface.Intelligence, CovenantOperationGateFixture.Digest(1),
            CovenantOperationGateFixture.Digest(2), input.HistoryWatermarkUtc, input.HistoryRevision, input.SensitivityRevision), default);

        Assert.True(acquired.IsSuccess, $"{acquired.Error.Code}: {acquired.Error.Message}");

        return acquired.Value;
    }
}
