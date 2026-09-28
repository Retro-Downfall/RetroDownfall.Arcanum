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
        ChangeLifecycleAsync(target, writeLease, LifecycleChange.Retire, cancellationToken);

    public Task<Result<LexiconCurationResult>> ReinstateAsync(
        LexiconCurationTarget target, CovenantWriteLease? writeLease, CancellationToken cancellationToken = default) =>
        ChangeLifecycleAsync(target, writeLease, LifecycleChange.Reinstate, cancellationToken);

    public Task<Result<LexiconCurationResult>> PinAsync(
        LexiconCurationTarget target, CovenantWriteLease? writeLease, CancellationToken cancellationToken = default) =>
        ChangeLifecycleAsync(target, writeLease, LifecycleChange.Pin, cancellationToken);

    public Task<Result<LexiconCurationResult>> UnpinAsync(
        LexiconCurationTarget target, CovenantWriteLease? writeLease, CancellationToken cancellationToken = default) =>
        ChangeLifecycleAsync(target, writeLease, LifecycleChange.Unpin, cancellationToken);

    private enum LifecycleChange
    {
        Retire,

        Reinstate,

        Pin,

        Unpin,
    }

    private async Task<Result<LexiconCurationResult>> ChangeLifecycleAsync(
        LexiconCurationTarget target, CovenantWriteLease? writeLease, LifecycleChange change, CancellationToken cancellationToken)
    {
        if (target is null || target.Validate().IsFailure)
        {
            return new Error(ErrorCodes.Lexicon.InvalidCurationTarget, "A complete Lexicon target is required.");
        }

        if (target.SensitivityLabel.IsPresent && writeLease is null)
        {
            return new Error(ErrorCodes.Lexicon.ProtectedMutationRefused,
                "A protected Lexicon lifecycle change requires an exact-scope write capability.");
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

                    bool pinChange = change is LifecycleChange.Pin or LifecycleChange.Unpin;

                    bool setTimestamp = change is LifecycleChange.Retire or LifecycleChange.Pin;

                    DateTimeOffset? timestamp = pinChange ? state.Row.Entry.PinnedAtUtc : state.Row.Entry.RetiredAtUtc;

                    LexiconCurationOutcomeKind outcome = change switch
                    {
                        LifecycleChange.Retire => LexiconCurationOutcomeKind.AlreadyRetired,
                        LifecycleChange.Reinstate => LexiconCurationOutcomeKind.NotRetired,
                        LifecycleChange.Pin => LexiconCurationOutcomeKind.AlreadyPinned,
                        _ => LexiconCurationOutcomeKind.NotPinned,
                    };

                    if ((timestamp is not null) != setTimestamp)
                    {
                        DateTimeOffset now = DateTimeOffset.UtcNow;

                        if (change == LifecycleChange.Retire)
                        {
                            await EnsureCurationBaselineAsync(connection, state, now, cancellationToken).ConfigureAwait(false);
                        }

                        await using (DbCommand command = connection.CreateCommand())
                        {
                            command.CommandText = $"""
                                UPDATE lexicon_entries SET {(pinChange ? "PinnedAtUtc" : "RetiredAtUtc")} = @timestamp,
                                    CurationGeneration = CurationGeneration + 1
                                WHERE Id = @id AND CurationGeneration = @generation
                                """;

                            AddParameter(command, "@timestamp", setTimestamp ? UtcInstantText.Format(now) : DBNull.Value);

                            AddParameter(command, "@id", target.EntryId.ToString("N"));

                            AddParameter(command, "@generation", target.CurationGeneration);

                            RequireIntegrity(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1);
                        }

                        if (change == LifecycleChange.Retire)
                        {
                            RequireIntegrity(await AnnalsClaimWriter.AppendRetirementAsync(connection, null,
                                AnnalSubjectStore.Lexicon, target.EntryId.ToString("N"), AnnalOrigin.OperatorStated,
                                target.Scope.Kind == LexiconScopeKind.Global ? SagaMemoryScopeKind.Global : SagaMemoryScopeKind.Campaign,
                                target.Scope.CampaignId?.ToString("D"), state.Label?.Sensitivity ?? ContentSensitivity.None,
                                now, now, null, cancellationToken).ConfigureAwait(false));
                        }
                        else if (change == LifecycleChange.Reinstate)
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
