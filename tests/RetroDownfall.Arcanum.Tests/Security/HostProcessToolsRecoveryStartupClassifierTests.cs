using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>The recovery-only host-tools decision over one marker sample and one durable row.</summary>
[Collection("Grimoire")]

[Trait("Category", "Integration")]
public sealed class HostProcessToolsRecoveryStartupClassifierTests(GrimoireFixture fixture)
{
    [SkippableFact]
    public async Task A_clean_exact_installation_mints_only_a_provisional_permitted_policy()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumDbContext context = fixture.CreateContext(fixture.CopyDatabase());

        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();

        HostProcessToolsAuthorityRow row = (await new HostProcessToolsAuthorityStore(connection)
            .ReadAsync(CancellationToken.None)).Value;

        FakeHostProcessToolsMarkerStore markers = new();

        FakeHostProcessToolsEnvironmentProbe environment = new() { EscapeHatchOptIn = false };

        HostProcessToolsRecoveryStartupClassifier classifier = new(
            markers,
            environment,
            new HostProcessToolsMarkerPairJoiner());

        Result<HostProcessToolsRecoveryMarkerSnapshot> marker = classifier.CaptureMarker();

        Assert.True(marker.IsSuccess, marker.Error.Message);

        Result<IHostProcessToolsRuntimePolicy> classified = await classifier.ClassifyAsync(
            connection,
            Guid.Parse(row.InstallationIdentity),
            marker.Value,
            CancellationToken.None);

        Assert.True(classified.IsSuccess, classified.Error.Message);

        Assert.True(classified.Value.IsPublished);

        Assert.True(classified.Value.CovenantPermitted);
    }

    [Fact]
    public void A_malformed_marker_refuses_before_any_database_is_opened()
    {
        FakeHostProcessToolsMarkerStore markers = new()
        {
            ReadStatusOverride = HostProcessToolsMarkerReadStatus.Malformed,
        };

        HostProcessToolsRecoveryStartupClassifier classifier = new(
            markers,
            new FakeHostProcessToolsEnvironmentProbe { EscapeHatchOptIn = false },
            new HostProcessToolsMarkerPairJoiner());

        Result<HostProcessToolsRecoveryMarkerSnapshot> captured = classifier.CaptureMarker();

        Assert.True(captured.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, captured.Error.Code);
    }

    /// <summary>
    /// A credential store that cannot be read right now is an outage, not a marker that disagrees:
    /// the start is retryable once the store is unlocked, so it keeps <c>Covenant.Unavailable</c>.
    /// </summary>
    [Fact]
    public void An_unavailable_marker_store_is_an_outage_not_manual_recovery()
    {
        FakeHostProcessToolsMarkerStore markers = new()
        {
            ReadStatusOverride = HostProcessToolsMarkerReadStatus.Unavailable,
        };

        HostProcessToolsRecoveryStartupClassifier classifier = new(
            markers,
            new FakeHostProcessToolsEnvironmentProbe { EscapeHatchOptIn = false },
            new HostProcessToolsMarkerPairJoiner());

        Result<HostProcessToolsRecoveryMarkerSnapshot> captured = classifier.CaptureMarker();

        Assert.True(captured.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, captured.Error.Code);
    }

    [SkippableFact]
    public async Task A_wrong_installation_or_a_clean_row_beside_a_marker_refuses()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumDbContext context = fixture.CreateContext(fixture.CopyDatabase());

        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();

        HostProcessToolsAuthorityRow row = (await new HostProcessToolsAuthorityStore(connection)
            .ReadAsync(CancellationToken.None)).Value;

        FakeHostProcessToolsMarkerStore markers = new();

        HostProcessToolsRecoveryStartupClassifier classifier = new(
            markers,
            new FakeHostProcessToolsEnvironmentProbe { EscapeHatchOptIn = false },
            new HostProcessToolsMarkerPairJoiner());

        HostProcessToolsRecoveryMarkerSnapshot absent = classifier.CaptureMarker().Value;

        Result<IHostProcessToolsRuntimePolicy> wrongInstallation = await classifier.ClassifyAsync(
            connection,
            Guid.NewGuid(),
            absent,
            CancellationToken.None);

        Assert.True(wrongInstallation.IsFailure);

        // Disagreement is evidence, not an outage: it stays the manual-recovery refusal.
        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, wrongInstallation.Error.Code);

        markers.SeedForeignMarker();

        HostProcessToolsRecoveryMarkerSnapshot foreign = classifier.CaptureMarker().Value;

        Result<IHostProcessToolsRuntimePolicy> mismatch = await classifier.ClassifyAsync(
            connection,
            Guid.Parse(row.InstallationIdentity),
            foreign,
            CancellationToken.None);

        Assert.True(mismatch.IsFailure);
    }

    /// <summary>
    /// A catalog another connection holds exclusively is an outage, not evidence. The identity read
    /// meets SQLITE_BUSY, wrapped in the bootstrapper's unavailable exception, and the start is
    /// retryable once the lock clears; the terminal finisher reports the same read the same way.
    /// </summary>
    [Fact]
    public async Task A_busy_catalog_during_the_identity_read_is_an_outage_not_manual_recovery()
    {
        Guid installationId = Guid.NewGuid();

        await using ExclusivelyLockedCatalog locked = await ExclusivelyLockedCatalog.CreateAsync(
            installationId,
            CancellationToken.None);

        HostProcessToolsRecoveryStartupClassifier classifier = new(
            new FakeHostProcessToolsMarkerStore(),
            new FakeHostProcessToolsEnvironmentProbe { EscapeHatchOptIn = false },
            new HostProcessToolsMarkerPairJoiner());

        Result<IHostProcessToolsRuntimePolicy> classified = await classifier.ClassifyAsync(
            locked.RecoveryConnection,
            installationId,
            classifier.CaptureMarker().Value,
            CancellationToken.None);

        Assert.True(classified.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, classified.Error.Code);
    }

    /// <summary>
    /// The durable authority-row read meets the same busy catalog the identity read can. It is an
    /// outage, not an unreadable row: the start is retryable. Any other storage failure still escapes
    /// to the caller, which disposes the probe and rethrows.
    /// </summary>
    [Theory]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public async Task A_busy_catalog_during_the_authority_row_read_is_an_outage_not_manual_recovery(
        int sqliteErrorCode,
        bool outage)
    {
        HostProcessToolsRecoveryStartupClassifier classifier = new(
            new FakeHostProcessToolsMarkerStore(),
            new FakeHostProcessToolsEnvironmentProbe { EscapeHatchOptIn = false },
            new HostProcessToolsMarkerPairJoiner());

        ThrowingAuthorityStore authority = new(
            new SqliteException("Injected authority-row read failure.", sqliteErrorCode));

        if (!outage)
        {
            await Assert.ThrowsAsync<SqliteException>(() => classifier.ClassifyAuthorityAsync(
                authority,
                classifier.CaptureMarker().Value,
                CancellationToken.None));

            return;
        }

        Result<IHostProcessToolsRuntimePolicy> classified = await classifier.ClassifyAuthorityAsync(
            authority,
            classifier.CaptureMarker().Value,
            CancellationToken.None);

        Assert.True(classified.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, classified.Error.Code);
    }

    [SkippableFact]
    public async Task An_escape_hatch_decision_refuses_recovery_without_publishing_the_real_policy()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumDbContext context = fixture.CreateContext(fixture.CopyDatabase());

        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();

        HostProcessToolsAuthorityRow row = (await new HostProcessToolsAuthorityStore(connection)
            .ReadAsync(CancellationToken.None)).Value;

        HostProcessToolsRecoveryStartupClassifier classifier = new(
            new FakeHostProcessToolsMarkerStore(),
            new FakeHostProcessToolsEnvironmentProbe { EscapeHatchOptIn = true },
            new HostProcessToolsMarkerPairJoiner());

        Result<IHostProcessToolsRuntimePolicy> result = await classifier.ClassifyAsync(
            connection,
            Guid.Parse(row.InstallationIdentity),
            classifier.CaptureMarker().Value,
            CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    /// <summary>An authority store whose every read throws one storage failure.</summary>
    private sealed class ThrowingAuthorityStore(Exception failure) : IHostProcessToolsAuthorityStore
    {
        public Task<Result<HostProcessToolsAuthorityRow>> ReadAsync(CancellationToken cancellationToken) =>
            throw failure;

        public Task<Result<HostProcessToolsAuthorityRow?>> TryReadAsync(CancellationToken cancellationToken) =>
            throw failure;

        public Task<Result<HostProcessToolsProtectedInventory>> InventoryProtectedStateAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Recovery classification never inventories.");

        public Task<Result> CommitPendingAsync(
            HostProcessToolsAuthorityRow expected,
            Guid transitionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Recovery classification never writes.");

        public Task<Result> CommitTaintedAsync(
            HostProcessToolsAuthorityRow expected,
            Guid transitionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Recovery classification never writes.");

        public Task<Result> CompensateToCleanAsync(
            HostProcessToolsAuthorityRow expected,
            Guid transitionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Recovery classification never writes.");
    }
}
