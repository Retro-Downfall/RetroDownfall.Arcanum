using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// The staging index is the only pointer back to a decrypted tree that does not sit beside the live
/// installation, and the client mutation boundary reads it before every mutation. What it must never
/// do is become a file its own reader refuses.
/// </summary>
public sealed class BackupRestoreStagingIndexTests : IDisposable
{

    /// <summary>
    /// One past the bound the reader enforces (1024, private to the index).
    /// </summary>
    /// <remarks>
    /// Written as a literal rather than derived from the production constant, deliberately: the
    /// property under test is that the writer and the reader agree on one number, and a test that read
    /// that number from the same place the writer does could not tell agreement from a shared mistake.
    /// </remarks>
    private const int OnePastTheReadersBound = 1025;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-staging-index-" + Guid.NewGuid().ToString("N"));

    private readonly string _live;

    public BackupRestoreStagingIndexTests()
    {

        _live = Path.Combine(_root, "arcanum");

        Directory.CreateDirectory(_live);

    }

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    /// <summary>
    /// An index that has been added to more times than the reader's bound is still readable, and still
    /// names the staging root the newest restore is about to create.
    /// </summary>
    /// <remarks>
    /// Entered through <c>Add</c>, which is what a restore calls before it creates each staging root,
    /// and <c>InspectAsync</c>, which is what the client mutation boundary calls before every
    /// mutation. Nothing in between is seeded: the file this reads is the file those writes produced.
    ///
    /// <para>The reader refuses an index carrying more than its bound as malformed, and that refusal
    /// is fail-closed all the way out to <c>ArcanumClientMutationBoundary</c> — so an index the writer
    /// was willing to produce and the reader will not accept does not degrade one restore, it refuses
    /// every client mutation on the installation until something rewrites the file.</para>
    ///
    /// <para>The newest root is asserted rather than only the count, because a trim that kept the
    /// oldest entries would satisfy a count and lose the one entry that still points at a decrypted
    /// tree.</para>
    /// </remarks>
    [Fact]
    public async Task An_index_grown_past_the_readers_bound_is_still_read_rather_than_refused()
    {

        string newest = string.Empty;

        for (int added = 0; added < OnePastTheReadersBound; added++)
        {

            newest = Path.Combine(_root, BackupRestoreJournal.CreateStagingName());

            BackupRestoreStagingIndex.Add(_live, newest);

        }

        Result<BackupRestoreStagingIndexRecord?> inspected =
            await BackupRestoreStagingIndex.InspectAsync(_live, CancellationToken.None);

        Assert.False(inspected.IsFailure);

        BackupRestoreStagingIndexRecord record = Assert.IsType<BackupRestoreStagingIndexRecord>(
            inspected.Value);

        Assert.Contains(newest, record.StagingRoots);

    }

}
