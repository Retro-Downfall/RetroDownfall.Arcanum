using System.Data.Common;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;

namespace RetroDownfall.Arcanum.Infrastructure.Lexicon;

internal sealed partial class LexiconService
{
    public Task<Result<LexiconCurationResult>> RetireAsync(
        LexiconCurationTarget target, CovenantWriteLease? writeLease, CancellationToken cancellationToken = default) =>
        ChangeRetirementAsync(target, writeLease, retire: true, cancellationToken);

    public Task<Result<LexiconCurationResult>> ReinstateAsync(
        LexiconCurationTarget target, CovenantWriteLease? writeLease, CancellationToken cancellationToken = default) =>
        ChangeRetirementAsync(target, writeLease, retire: false, cancellationToken);

    private async Task<Result<LexiconCurationResult>> ChangeRetirementAsync(
        LexiconCurationTarget target, CovenantWriteLease? writeLease, bool retire, CancellationToken cancellationToken)
    {
        if (target is null || target.Validate().IsFailure)
        {
            return new Error(ErrorCodes.Lexicon.InvalidCurationTarget, "A complete Lexicon target is required.");
        }

        try
        {
            Require(await ValidateCurationLeaseAsync(writeLease, target.Scope, cancellationToken).ConfigureAwait(false));

            return await SqliteBusyRetry.ExecuteAsync(async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await ExecuteNonQueryAsync(connection, cancellationToken, "BEGIN IMMEDIATE").ConfigureAwait(false);

                try
                {
                    CurationState state = await ReadCurationStateAsync(connection, target, cancellationToken).ConfigureAwait(false);

                    if (!TargetsEqual(target, state.Detail.Target))
                    {
                        throw new InspectionException(StaleTargetError);
                    }

                    if (state.Row.Entry.CurationGeneration == long.MaxValue)
                    {
                        throw new InspectionException(new Error(ErrorCodes.Lexicon.CurationGenerationExhausted,
                            "The Lexicon curation generation is exhausted."));
                    }

                    if (state.Label is not null && writeLease is null)
                    {
                        throw new InspectionException(new Error(ErrorCodes.Lexicon.ProtectedMutationRefused,
                            "A protected Lexicon lifecycle change requires an exact-scope write capability."));
                    }

                    LexiconCurationOutcomeKind outcome = retire
                        ? LexiconCurationOutcomeKind.AlreadyRetired : LexiconCurationOutcomeKind.NotRetired;

                    if ((state.Row.Entry.RetiredAtUtc is not null) != retire)
                    {
                        DateTimeOffset now = DateTimeOffset.UtcNow;

                        if (retire)
                        {
                            await EnsureCurationBaselineAsync(connection, state, now, cancellationToken).ConfigureAwait(false);
                        }

                        await using (DbCommand command = connection.CreateCommand())
                        {
                            command.CommandText = """
                                UPDATE lexicon_entries SET RetiredAtUtc = @retired,
                                    CurationGeneration = CurationGeneration + 1
                                WHERE Id = @id AND CurationGeneration = @generation
                                """;

                            AddParameter(command, "@retired", retire ? UtcInstantText.Format(now) : DBNull.Value);

                            AddParameter(command, "@id", target.EntryId.ToString("N"));

                            AddParameter(command, "@generation", target.CurationGeneration);

                            RequireIntegrity(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1);
                        }

                        if (retire)
                        {
                            RequireIntegrity(await AnnalsClaimWriter.AppendRetirementAsync(connection, null,
                                AnnalSubjectStore.Lexicon, target.EntryId.ToString("N"), AnnalOrigin.OperatorStated,
                                target.Scope.Kind == LexiconScopeKind.Global ? SagaMemoryScopeKind.Global : SagaMemoryScopeKind.Campaign,
                                target.Scope.CampaignId?.ToString("D"), state.Label?.Sensitivity ?? ContentSensitivity.None,
                                now, now, null, cancellationToken).ConfigureAwait(false));
                        }
                        else
                        {
                            _ = await AppendCurationContentAsync(connection, state, state.Row.Canonical,
                                AnnalOrigin.OperatorStated, now, now, cancellationToken).ConfigureAwait(false);
                        }

                        state = await ReadCurationStateAsync(connection, target, cancellationToken).ConfigureAwait(false);

                        outcome = LexiconCurationOutcomeKind.Applied;
                    }

                    Require(await ValidateCurationLeaseAsync(writeLease, target.Scope, cancellationToken).ConfigureAwait(false));

                    await ExecuteNonQueryAsync(connection, cancellationToken, "COMMIT").ConfigureAwait(false);

                    return Result<LexiconCurationResult>.Success(new(outcome, state.Detail));
                }
                catch
                {
                    await TryRollbackAsync(connection, "lifecycle").ConfigureAwait(false);

                    throw;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (InspectionException exception)
        {
            return exception.Error;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Lexicon lifecycle change failed.");

            return new Error(ErrorCodes.Lexicon.WriteFailed, "The Lexicon lifecycle change could not be persisted.");
        }
    }
}
