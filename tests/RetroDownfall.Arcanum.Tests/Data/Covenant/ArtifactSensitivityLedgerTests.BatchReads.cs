using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed partial class ArtifactSensitivityLedgerTests
{
    [Fact]
    public async Task Batch_read_returns_only_requested_kind_and_labels_in_input_order()
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        Guid first = Guid.NewGuid();

        Guid second = Guid.NewGuid();

        Guid missing = Guid.NewGuid();

        foreach (Guid id in new[] { first, second })
        {
            Assert.True((await fixture.Ledger.LabelAsync(Tainted(SensitiveArtifactKind.Lexicon, id, GenerationOne), CancellationToken.None)).IsSuccess);
        }

        Assert.True((await fixture.Ledger.LabelAsync(Tainted(SensitiveArtifactKind.Saga, missing, GenerationOne), CancellationToken.None)).IsSuccess);

        IArtifactSensitivityLedger ledger = fixture.Ledger;

        using SqliteStatementTrace trace = new(fixture.Connection);

        Result<IReadOnlyList<ArtifactSensitivityLabel>> read = await ledger.ReadLabelsAsync(
            SensitiveArtifactKind.Lexicon, [second, missing, first, second], CancellationToken.None);

        Assert.True(read.IsSuccess, read.IsFailure ? read.Error.Message : null);

        Assert.Equal(new[] { second, first, second }, read.Value.Select(static label => label.ArtifactId));

        Assert.All(read.Value, static label => Assert.Equal(SensitiveArtifactKind.Lexicon, label.ArtifactKind));

        Assert.Single(trace.Statements, static statement => statement.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(257, true)]
    public async Task Empty_and_oversized_batches_do_not_acquire_a_connection(int count, bool failure)
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        RecordingConnectionSource source = new(fixture.Connection);

        IArtifactSensitivityLedger ledger = new ArtifactSensitivityLedger(source);

        Guid[] ids = Enumerable.Range(0, count).Select(static _ => Guid.NewGuid()).ToArray();

        Result<IReadOnlyList<ArtifactSensitivityLabel>> result = await ledger.ReadLabelsAsync(
            SensitiveArtifactKind.Saga, ids, CancellationToken.None);

        Assert.Equal(failure, result.IsFailure);

        Assert.Equal(0, source.LatchingCalls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(256)]
    public async Task Resolving_an_unlabeled_page_uses_one_native_label_query(int targets)
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        // An unlabeled page must return before acquiring any erasure authority or kernel. These
        // collaborators deliberately cannot be used; the label read is the real native implementation.
        CovenantSensitiveRetentionPurgeCoordinator coordinator = new(
            fixture.Ledger,
            connections: null!,
            managedFileRequests: null!,
            gate: null!,
            issuer: null!,
            availability: null!,
            artifacts: null!,
            managedFiles: null!,
            authorityScope: new CovenantSensitivePurgeAuthorityScope(),
            logger: NullLogger<CovenantSensitiveRetentionPurgeCoordinator>.Instance);

        CovenantSensitivePurgeTarget[] page = Enumerable.Range(0, targets)
            .Select(static _ => new CovenantSensitivePurgeTarget(SensitiveArtifactKind.Saga, Guid.NewGuid()))
            .ToArray();

        using SqliteStatementTrace trace = new(fixture.Connection);

        Result<CovenantSensitivePurgeOutcome> result = await coordinator.PurgeAsync(page, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.True(result.Value.AllUnlabeled);

        Assert.Equal(targets, result.Value.Results.Count);

        Assert.Equal(0, trace.ImmediateTransactions);

        Assert.Single(trace.Statements, static statement =>
            statement.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && statement.Contains("artifact_sensitivity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_malformed_selected_label_refuses_the_batch_without_hiding_valid_labels()
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        Guid valid = Guid.NewGuid();

        Guid malformed = Guid.NewGuid();

        foreach (Guid id in new[] { valid, malformed })
        {
            Assert.True((await fixture.Ledger.LabelAsync(Tainted(SensitiveArtifactKind.Lexicon, id, GenerationOne), CancellationToken.None)).IsSuccess);
        }

        await using SqliteCommand corrupt = fixture.Connection.CreateCommand();

        corrupt.CommandText = "UPDATE artifact_sensitivity SET CreatedAtUtc = $canary WHERE ArtifactId = $artifact;";

        corrupt.Parameters.AddWithValue("$canary", "PRIVATE-BATCH-LABEL\r\nforged-diagnostic");

        corrupt.Parameters.AddWithValue("$artifact", malformed.ToString("D").ToUpperInvariant());

        Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());

        IArtifactSensitivityLedger ledger = fixture.Ledger;

        Result<IReadOnlyList<ArtifactSensitivityLabel>> refused = await ledger.ReadLabelsAsync(
            SensitiveArtifactKind.Lexicon, [valid, malformed], CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, refused.Error.Code);

        Assert.Equal(CovenantInventoryFailureReason.MalformedLabel, refused.Error.CovenantInventoryFailure?.Reason);

        Assert.DoesNotContain("PRIVATE", refused.Error.Message, StringComparison.Ordinal);

        Result<IReadOnlyList<ArtifactSensitivityLabel>> unrelated = await ledger.ReadLabelsAsync(
            SensitiveArtifactKind.Lexicon, [valid], CancellationToken.None);

        Assert.True(unrelated.IsSuccess);

        Assert.Equal(valid, Assert.Single(unrelated.Value).ArtifactId);
    }

    [Fact]
    public async Task A_canceled_batch_never_acquires_a_connection()
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        RecordingConnectionSource source = new(fixture.Connection);

        IArtifactSensitivityLedger ledger = new ArtifactSensitivityLedger(source);

        using CancellationTokenSource cancellation = new();

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ledger.ReadLabelsAsync(
            SensitiveArtifactKind.Saga, [Guid.NewGuid()], cancellation.Token));

        Assert.Equal(0, source.LatchingCalls);
    }
}
