using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Issue #117 — reads the label table so a legacy raw delete can refuse an artifact it cannot erase
/// correctly.
/// </summary>
/// <remarks>
/// A reader and nothing else. It resolves no policy, acquires no lease, and removes nothing: the only
/// question it answers is whether a live label exists, and the only thing a caller may do with the
/// answer is stop.
/// </remarks>
internal sealed class CovenantLabeledArtifactGuard(
    IArtifactSensitivityLedger labels,
    ICovenantConnectionSource connections) : ICovenantLabeledArtifactGuard
{

    public async ValueTask<Result> EnsureUnlabeledAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {

        Result<ArtifactSensitivityLabel?> label;

        try
        {

            label = await labels
                .TryReadLabelAsync(kind, artifactId, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (SqliteException)
        {

            // No label table: nothing protected exists on this installation, so there is nothing to
            // guard and the ordinary delete is the whole operation.
            return Result.Success();

        }

        if (label.IsFailure)
        {

            return label.Error;

        }

        return label.Value is null
            ? Result.Success()
            : Refusal(kind);

    }

    public async ValueTask<Result> EnsureNoneLabeledAsync(
        SensitiveArtifactKind kind,
        CancellationToken cancellationToken = default)
    {

        try
        {

            SqliteConnection connection = await connections
                .GetOpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM artifact_sensitivity WHERE ArtifactKindCode = $kind);
                """;

            _ = command.Parameters.AddWithValue("$kind", (long)kind);

            object? any = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return any is 0L or null or DBNull
                ? Result.Success()
                : Refusal(kind);

        }
        catch (SqliteException)
        {

            return Result.Success();

        }

    }

    private static Error Refusal(SensitiveArtifactKind kind) =>
        new(
            ErrorCodes.Covenant.ForbiddenAuthority,
            $"A labelled {kind} artifact cannot be removed through a raw delete; it must be dispatched "
                + "through the sensitivity purge boundary so its evidence is preserved.");

}
