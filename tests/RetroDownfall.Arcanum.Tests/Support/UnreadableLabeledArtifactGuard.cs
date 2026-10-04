using System.Data.Common;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A labelled-artifact guard whose label table cannot be read, which answers every question with the
/// refusal the real guard gives for that condition.
/// </summary>
/// <remarks>
/// The real guard reaches this answer only when a read fails, and a delete's own read and the guard's
/// are two reads of one table, so no route test can make the second fail on its own without a stand-in.
/// Registered in place of the composition's guard, this puts the unreadable answer in front of every
/// caller that asks, and nothing else about the composition changes. It answers the way the real guard
/// does, <c>Covenant.Unavailable</c>, so a caller that mishandles that answer fails here as it would in
/// production.
/// </remarks>
internal sealed class UnreadableLabeledArtifactGuard : ICovenantLabeledArtifactTransactionGuard
{
    /// <summary>The refusal every question is answered with.</summary>
    internal static Error Unavailable { get; } = new(
        ErrorCodes.Covenant.Unavailable,
        "The sensitivity labels could not be read, so a raw delete cannot be shown to leave no labelled "
            + "artifact behind and was refused.");

    /// <summary>How many questions the guard has been asked.</summary>
    internal int Questions { get; private set; }

    public ValueTask<Result> EnsureUnlabeledAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        CancellationToken cancellationToken = default) =>
        Refuse();

    public ValueTask<Result> EnsureUnlabeledAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default) =>
        Refuse();

    public ValueTask<Result> EnsureAllUnlabeledAsync(
        SensitiveArtifactKind kind,
        IReadOnlyCollection<Guid> artifactIds,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default) =>
        Refuse();

    public ValueTask<Result> EnsureNoneLabeledAsync(
        SensitiveArtifactKind kind,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default) =>
        Refuse();

    private ValueTask<Result> Refuse()
    {
        Questions++;

        return ValueTask.FromResult(Result.Failure(Unavailable));
    }
}
