using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// How a restore reconciles authority and disclosure evidence with the machine it lands on.
/// </summary>
/// <remarks>
/// Both joins are monotonic in the destination's favour. An archive taken on a clean machine cannot
/// clear this machine's host-tools taint, and an archive with a smaller disclosure count cannot
/// reduce what this installation has already reported as having left it (§10.13, §10.16).
/// </remarks>
public sealed class CovenantRestoreStateJoinerTests : IAsyncLifetime
{

    private CovenantSchemaScratchDatabase _database = null!;

    public async Task InitializeAsync()
    {

        _database = await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await _database.InstallCoreObjectsAsync(
            ["external_disclosure_state", "external_disclosure_state_guard_delete"],
            CancellationToken.None);

    }

    [Fact]
    public void A_tainted_destination_stays_tainted_no_matter_what_the_archive_carries()
    {

        Result<CovenantAuthorityStateRow> joined = CovenantAuthorityStateJoiner.Join(
            Row(CovenantHostToolsState.HostToolsTainted, epoch: 4),
            Row(CovenantHostToolsState.Clean, epoch: 9),
            "9F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90");

        Assert.True(joined.IsSuccess, Describe(joined));

        // The escape happened here. A file copied in from a machine where it did not happen is not
        // evidence that it did not happen here.
        Assert.Equal(CovenantHostToolsState.HostToolsTainted, joined.Value.HostToolsState);

        Assert.NotNull(joined.Value.TransitionId);

    }

    [Fact]
    public void A_tainted_archive_taints_a_clean_destination()
    {

        Result<CovenantAuthorityStateRow> joined = CovenantAuthorityStateJoiner.Join(
            Row(CovenantHostToolsState.Clean, epoch: 4),
            Row(CovenantHostToolsState.HostToolsTainted, epoch: 2),
            "9F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90");

        Assert.True(joined.IsSuccess, Describe(joined));

        // Monotone in the other direction too: restoring content that was exposed elsewhere brings
        // that exposure with it.
        Assert.Equal(CovenantHostToolsState.HostToolsTainted, joined.Value.HostToolsState);

    }

    [Fact]
    public void The_authority_epoch_advances_past_both_lineages()
    {

        Result<CovenantAuthorityStateRow> joined = CovenantAuthorityStateJoiner.Join(
            Row(CovenantHostToolsState.Clean, epoch: 4),
            Row(CovenantHostToolsState.Clean, epoch: 11),
            "9F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90");

        // Every read authority issued under either lineage has to stop being valid.
        Assert.Equal(12, joined.Value.AuthorityEpoch);

        // And the restored installation carries its own identity, never the archive's.
        Assert.Equal("9F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90", joined.Value.InstallationIdentity);

    }

    [Fact]
    public void A_join_without_an_established_destination_identity_is_refused()
    {

        Assert.True(CovenantAuthorityStateJoiner.Join(
            Row(CovenantHostToolsState.Clean, epoch: 1),
            Row(CovenantHostToolsState.Clean, epoch: 1),
            "   ").IsFailure);

        Assert.True(CovenantAuthorityStateJoiner.Join(
            null!,
            Row(CovenantHostToolsState.Clean, epoch: 1),
            "9F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90").IsFailure);

    }

    [Fact]
    public async Task Disclosure_counts_join_to_a_lower_bound_and_never_shrink()
    {

        await WriteStagedBucketAsync(count: 3, everOccurred: true, timestamp: 100);

        CovenantDisclosureState destination = Bucket(count: 7, everOccurred: true, timestamp: 250);

        await using SqliteTransaction transaction =
            (SqliteTransaction)await _database.Connection.BeginTransactionAsync(CancellationToken.None);

        Result<int> joined = await CovenantDisclosureStateJoiner.JoinIntoStagedAsync(
            _database.Connection,
            transaction,
            [destination],
            TimeProvider.System,
            CancellationToken.None);

        Assert.True(joined.IsSuccess, Describe(joined));

        await transaction.CommitAsync(CancellationToken.None);

        List<CovenantDisclosureState> after = await CovenantDisclosureStateJoiner.ReadAllAsync(
            _database.Connection,
            null,
            CancellationToken.None);

        CovenantDisclosureState result = Assert.Single(after);

        // The larger of the two, reported as a lower bound. Adding them would double-count effects
        // the two installations share; taking the smaller would understate what left.
        Assert.Equal(7UL, result.Count);
        Assert.Equal(CovenantDisclosureCountKind.LowerBound, result.CountKind);
        Assert.Equal(250, result.MaximumTimestamp);
        Assert.True(result.EverOccurred);

    }

    [Fact]
    public async Task An_archive_bucket_the_destination_never_had_is_still_carried_forward()
    {

        CovenantDisclosureState destination = Bucket(count: 0, everOccurred: false, timestamp: 0);

        await using SqliteTransaction transaction =
            (SqliteTransaction)await _database.Connection.BeginTransactionAsync(CancellationToken.None);

        _ = await CovenantDisclosureStateJoiner.JoinIntoStagedAsync(
            _database.Connection,
            transaction,
            [destination],
            TimeProvider.System,
            CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

        Assert.Single(await CovenantDisclosureStateJoiner.ReadAllAsync(
            _database.Connection,
            null,
            CancellationToken.None));

    }

    public async Task DisposeAsync()
    {

        if (_database is not null)
        {

            await _database.DisposeAsync();

        }

    }

    private static CovenantAuthorityStateRow Row(CovenantHostToolsState state, long epoch) =>
        new(
            "1F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90",
            epoch,
            state,
            state == CovenantHostToolsState.Clean ? null : 2,
            state == CovenantHostToolsState.Clean ? null : new byte[32],
            state == CovenantHostToolsState.Clean ? null : Guid.NewGuid().ToString("D"));

    private static CovenantDisclosureState Bucket(ulong count, bool everOccurred, long timestamp) =>
        new(
            CovenantEgressDestination.EncryptedBackup,
            CovenantDisclosureRevocability.Nonrevocable,
            everOccurred ? CovenantDisclosureCountKind.Exact : CovenantDisclosureCountKind.Exact,
            everOccurred,
            count,
            timestamp,
            everOccurred ? Bloom() : new byte[32]);

    private static byte[] Bloom() => [.. Enumerable.Repeat((byte)0x0F, 32)];

    private static string Describe<T>(Result<T> result) =>
        result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : string.Empty;

    private async Task WriteStagedBucketAsync(ulong count, bool everOccurred, long timestamp)
    {

        await using SqliteTransaction transaction =
            (SqliteTransaction)await _database.Connection.BeginTransactionAsync(CancellationToken.None);

        _ = await CovenantDisclosureStateJoiner.JoinIntoStagedAsync(
            _database.Connection,
            transaction,
            [Bucket(count, everOccurred, timestamp)],
            TimeProvider.System,
            CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

    }

}
