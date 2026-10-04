using System.Data.Common;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// How many questions of each shape the labelled-artifact guard was asked, shared across the guards a
/// host builds for each request scope.
/// </summary>
internal sealed class GuardQuestionCounts
{
    private int _perArtifact;

    private int _batches;

    private int _batchedArtifacts;

    private int _bulk;

    private int _outsideTransaction;

    /// <summary>Questions about one named artifact, asked inside a transaction.</summary>
    internal int PerArtifact => Volatile.Read(ref _perArtifact);

    /// <summary>Questions about a set of artifacts, asked inside a transaction.</summary>
    internal int Batches => Volatile.Read(ref _batches);

    /// <summary>How many artifacts those batched questions named between them.</summary>
    internal int BatchedArtifacts => Volatile.Read(ref _batchedArtifacts);

    /// <summary>Whole-kind questions.</summary>
    internal int Bulk => Volatile.Read(ref _bulk);

    /// <summary>Questions asked with no transaction.</summary>
    internal int OutsideTransaction => Volatile.Read(ref _outsideTransaction);

    internal void CountPerArtifact() => Interlocked.Increment(ref _perArtifact);

    internal void CountBatch(int artifacts)
    {
        _ = Interlocked.Increment(ref _batches);

        _ = Interlocked.Add(ref _batchedArtifacts, artifacts);
    }

    internal void CountBulk() => Interlocked.Increment(ref _bulk);

    internal void CountOutsideTransaction() => Interlocked.Increment(ref _outsideTransaction);
}

/// <summary>
/// The real labelled-artifact guard, counting every question it is asked and passing each on unchanged.
/// </summary>
/// <remarks>
/// A decorator and not a stand-in: the answers are the real guard's, so a test that counts questions is
/// also a test that the real answers still come back.
/// </remarks>
internal sealed class CountingLabeledArtifactGuard(
    ICovenantLabeledArtifactTransactionGuard inner,
    GuardQuestionCounts counts) : ICovenantLabeledArtifactTransactionGuard
{
    public ValueTask<Result> EnsureUnlabeledAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        counts.CountOutsideTransaction();

        return inner.EnsureUnlabeledAsync(kind, artifactId, cancellationToken);
    }

    public ValueTask<Result> EnsureUnlabeledAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        counts.CountPerArtifact();

        return inner.EnsureUnlabeledAsync(kind, artifactId, connection, transaction, cancellationToken);
    }

    public ValueTask<Result> EnsureAllUnlabeledAsync(
        SensitiveArtifactKind kind,
        IReadOnlyCollection<Guid> artifactIds,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        counts.CountBatch(artifactIds.Count);

        return inner.EnsureAllUnlabeledAsync(kind, artifactIds, connection, transaction, cancellationToken);
    }

    public ValueTask<Result> EnsureNoneLabeledAsync(
        SensitiveArtifactKind kind,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        counts.CountBulk();

        return inner.EnsureNoneLabeledAsync(kind, connection, transaction, cancellationToken);
    }
}
