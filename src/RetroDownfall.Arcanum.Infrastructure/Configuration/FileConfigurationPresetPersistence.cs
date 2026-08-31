using System.Collections.Immutable;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Configuration.Presets;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Configuration;

internal sealed class ConfigurationPresetPersistenceHooks
{

    public Func<bool>? ContinueAfterConfigurationWrite { get; init; }

}

internal sealed class FileConfigurationPresetPersistence(
    ConfigurationWriter writer,
    ConfigurationValidator validator,
    ILogger<FileConfigurationPresetPersistence> logger,
    ConfigurationPresetPersistenceHooks? hooks = null) : IConfigurationPresetPersistence
{

    internal const int MaxSidecarBytes = 1024 * 1024;

    public Task<Result<ConfigurationPresetSnapshot>> ReadAsync(
        CancellationToken cancellationToken = default) =>
        RunTransactionAsync(
            () => ReadUnderTransactionAsync(cancellationToken),
            cancellationToken);

    public Task<Result<ConfigurationPresetSnapshot>> PeekAsync(
        CancellationToken cancellationToken = default) =>
        RunTransactionAsync(
            () => PeekUnderTransactionAsync(cancellationToken),
            cancellationToken);

    public Task<Result<ConfigurationPresetCommitResult>> ApplyAsync(
        ConfigurationPresetCommitRequest request,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        return RunTransactionAsync(
            () => ApplyUnderTransactionAsync(request, cancellationToken),
            cancellationToken);

    }

    public Task<Result<ConfigurationPresetResetCommitResult>> ResetAsync(
        ConfigurationPresetResetCommitRequest request,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        return RunTransactionAsync(
            () => ResetUnderTransactionAsync(request, cancellationToken),
            cancellationToken);

    }

    /// <summary>
    /// Runs a preset operation under the shared configuration transaction. A bounded acquisition
    /// that expires is a domain failure, not an exception escaping a <see cref="Result{T}"/> API.
    /// </summary>
    private static async Task<Result<T>> RunTransactionAsync<T>(
        Func<Task<Result<T>>> operation,
        CancellationToken cancellationToken)
    {

        try
        {

            return await ArcanumConfigurationTransaction
                .RunAsync(operation, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (ArcanumConfigurationLockException exception)
        {

            return Result<T>.Failure(
                new Error("Preset.LockUnavailable", exception.Message));

        }

    }

    private async Task<Result<ConfigurationPresetSnapshot>> ReadUnderTransactionAsync(
        CancellationToken cancellationToken)
    {

        try
        {

            Result recovery = await RecoverPreparedTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            return recovery.IsFailure
                ? Result<ConfigurationPresetSnapshot>.Failure(recovery.Error)
                : await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {

            logger.LogError(exception, "Preset state read or recovery failed.");

            return Result<ConfigurationPresetSnapshot>.Failure(
                new Error("Preset.RecoveryFailed", exception.Message));

        }

    }

    private async Task<Result<ConfigurationPresetSnapshot>> PeekUnderTransactionAsync(
        CancellationToken cancellationToken)
    {

        try
        {

            using SecureFileReadResult journal = await SecureFileReader.ReadBytesAsync(
                    ArcanumPaths.ConfigurationPresetJournalFile,
                    MaxSidecarBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            if (journal.Status != SecureFileReadStatus.NotFound)
            {

                return Result<ConfigurationPresetSnapshot>.Failure(
                    new Error(
                        "Preset.RecoveryRequired",
                        "A prepared preset transaction requires exclusive recovery before setup can continue."));

            }

            return await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {

            logger.LogError(exception, "Preset state peek failed.");

            return Result<ConfigurationPresetSnapshot>.Failure(
                new Error("Preset.ReadFailed", exception.Message));

        }

    }

    private async Task<Result<ConfigurationPresetCommitResult>> ApplyUnderTransactionAsync(
        ConfigurationPresetCommitRequest request,
        CancellationToken cancellationToken)
    {

        ConfigurationPresetJournalDocument? journal = null;

        bool configurationWritten = false;

        try
        {

            Result recovery = await RecoverPreparedTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            if (recovery.IsFailure)
            {

                return Result<ConfigurationPresetCommitResult>.Failure(recovery.Error);

            }

            Result<ConfigurationPresetSnapshot> currentResult =
                await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);

            if (currentResult.IsFailure)
            {

                return Result<ConfigurationPresetCommitResult>.Failure(currentResult.Error);

            }

            ConfigurationPresetSnapshot current = currentResult.Value;

            if (!MatchesExpectedSettings(current.PersistedSettings, request.ExpectedCurrentHash))
            {

                return Result<ConfigurationPresetCommitResult>.Failure(ConfigurationChanged());

            }

            Result provenanceValidation = ValidateApplyProvenance(
                request.Provenance,
                current.PersistedSettings,
                request.CandidateSettings);

            if (provenanceValidation.IsFailure)
            {

                return Result<ConfigurationPresetCommitResult>.Failure(
                    provenanceValidation.Error);

            }

            Result validation = await ValidateCandidateAsync(
                    request.CandidateSettings,
                    cancellationToken)
                .ConfigureAwait(false);

            if (validation.IsFailure)
            {

                return Result<ConfigurationPresetCommitResult>.Failure(validation.Error);

            }

            journal = CreateJournal(
                "apply",
                current.PersistedSettings,
                request.CandidateSettings,
                request.Provenance.BaselineValues.Select(static value => value.Path),
                current.Provenance,
                request.Provenance);

            Result prepared = await PrepareApplyAsync(
                    journal,
                    request.Provenance,
                    cancellationToken)
                .ConfigureAwait(false);

            if (prepared.IsFailure)
            {

                return Result<ConfigurationPresetCommitResult>.Failure(prepared.Error);

            }

            Result<ArcanumSettings> write = await writer.UpdateAsync(
                    current.PersistedSettings,
                    latest =>
                    {

                        if (!MatchesExpectedSettings(latest, request.ExpectedCurrentHash))
                        {

                            return Result<ArcanumSettings>.Failure(ConfigurationChanged());

                        }

                        return Result<ArcanumSettings>.Success(request.CandidateSettings);

                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (write.IsFailure)
            {

                return Result<ConfigurationPresetCommitResult>.Failure(
                    await HandleConfigurationWriteFailureAsync(
                            journal,
                            write.Error,
                            CancellationToken.None)
                        .ConfigureAwait(false));

            }

            configurationWritten = true;

            if (hooks?.ContinueAfterConfigurationWrite?.Invoke() == false)
            {

                Error interrupted = new(
                    "Preset.ApplyFailed",
                    "Preset application was interrupted after the configuration write.");

                Error rolledBack = await RollBackPreparedAsync(
                        journal,
                        interrupted,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested
                    && IsRollbackFailure(rolledBack))
                {

                    return Result<ConfigurationPresetCommitResult>.Failure(rolledBack);

                }

                journal = null;

                cancellationToken.ThrowIfCancellationRequested();

                return Result<ConfigurationPresetCommitResult>.Failure(rolledBack);

            }

            Result stateWrite = await WriteProvenanceAsync(
                    ArcanumPaths.ConfigurationPresetStateFile,
                    request.Provenance,
                    cancellationToken)
                .ConfigureAwait(false);

            if (stateWrite.IsFailure)
            {

                return Result<ConfigurationPresetCommitResult>.Failure(
                    await RollBackPreparedAsync(
                            journal,
                            stateWrite.Error,
                            CancellationToken.None)
                        .ConfigureAwait(false));

            }

            ArcanumSettings verified = ConfigurationBootstrapper.LoadPersistedArcanumSettings();

            if (!ValuesMatch(verified, journal.CandidateValues))
            {

                return Result<ConfigurationPresetCommitResult>.Failure(
                    await RollBackPreparedAsync(
                            journal,
                            new Error(
                                "Preset.VerificationFailed",
                                "Preset-owned configuration verification did not match the candidate snapshot."),
                            CancellationToken.None)
                        .ConfigureAwait(false));

            }

            TryDeleteKnownFile(ArcanumPaths.ConfigurationPresetJournalFile);

            journal = null;

            Result<ConfigurationPresetSnapshot> snapshot =
                await ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);

            return snapshot.IsSuccess
                ? Result<ConfigurationPresetCommitResult>.Success(
                    new ConfigurationPresetCommitResult(
                        ConfigurationPresetRollbackStatus.SnapshotCreated,
                        snapshot.Value))
                : Result<ConfigurationPresetCommitResult>.Failure(snapshot.Error);

        }
        catch (OperationCanceledException)
        {

            Error cancelled = new(
                "Preset.ApplyCancelled",
                "Preset application was cancelled.");

            if (journal is not null && configurationWritten)
            {

                Error rolledBack = await RollBackPreparedAsync(
                        journal,
                        cancelled,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                if (IsRollbackFailure(rolledBack))
                {

                    return Result<ConfigurationPresetCommitResult>.Failure(rolledBack);

                }

            }
            else if (journal is not null)
            {

                Result cleanup = await RestoreSidecarsAndDeleteJournalAsync(
                        journal.PreviousProvenance,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                if (cleanup.IsFailure)
                {

                    return Result<ConfigurationPresetCommitResult>.Failure(
                        RollbackFailed("apply", cancelled));

                }

            }

            throw;

        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {

            logger.LogError(exception, "Preset apply transaction failed.");

            Error failure = new("Preset.ApplyFailed", exception.Message);

            if (journal is not null && configurationWritten)
            {

                failure = await RollBackPreparedAsync(
                        journal,
                        failure,
                        CancellationToken.None)
                    .ConfigureAwait(false);

            }
            else if (journal is not null)
            {

                Result cleanup = await RestoreSidecarsAndDeleteJournalAsync(
                        journal.PreviousProvenance,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                if (cleanup.IsFailure)
                {

                    failure = RollbackFailed("apply", failure);

                }

            }

            return Result<ConfigurationPresetCommitResult>.Failure(failure);

        }

    }

    private async Task<Result<ConfigurationPresetResetCommitResult>> ResetUnderTransactionAsync(
        ConfigurationPresetResetCommitRequest request,
        CancellationToken cancellationToken)
    {

        ConfigurationPresetJournalDocument? journal = null;

        bool configurationWritten = false;

        try
        {

            Result recovery = await RecoverPreparedTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            if (recovery.IsFailure)
            {

                return Result<ConfigurationPresetResetCommitResult>.Failure(recovery.Error);

            }

            Result<ConfigurationPresetSnapshot> currentResult =
                await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);

            if (currentResult.IsFailure)
            {

                return Result<ConfigurationPresetResetCommitResult>.Failure(currentResult.Error);

            }

            ConfigurationPresetSnapshot current = currentResult.Value;

            if (current.Provenance is null
                || !ProvenanceEquals(current.Provenance, request.Provenance))
            {

                return Result<ConfigurationPresetResetCommitResult>.Failure(
                    new Error(
                        "Preset.NoActivePreset",
                        "No matching active preset provenance is available to reset."));

            }

            if (!MatchesExpectedSettings(current.PersistedSettings, request.ExpectedCurrentHash))
            {

                return Result<ConfigurationPresetResetCommitResult>.Failure(ConfigurationChanged());

            }

            Result<ResetCandidate> resetCandidate = BuildResetCandidate(
                current.PersistedSettings,
                request.Provenance);

            if (resetCandidate.IsFailure)
            {

                return Result<ConfigurationPresetResetCommitResult>.Failure(resetCandidate.Error);

            }

            Result validation = await ValidateCandidateAsync(
                    resetCandidate.Value.Settings,
                    cancellationToken)
                .ConfigureAwait(false);

            if (validation.IsFailure)
            {

                return Result<ConfigurationPresetResetCommitResult>.Failure(validation.Error);

            }

            journal = CreateJournal(
                "reset",
                current.PersistedSettings,
                resetCandidate.Value.Settings,
                request.Provenance.BaselineValues.Select(static value => value.Path),
                current.Provenance,
                nextProvenance: null);

            Result journalWrite = await WriteJournalAsync(journal, cancellationToken)
                .ConfigureAwait(false);

            if (journalWrite.IsFailure)
            {

                return Result<ConfigurationPresetResetCommitResult>.Failure(journalWrite.Error);

            }

            Result<ArcanumSettings> write = await writer.UpdateAsync(
                    current.PersistedSettings,
                    latest =>
                    {

                        if (!MatchesExpectedSettings(latest, request.ExpectedCurrentHash))
                        {

                            return Result<ArcanumSettings>.Failure(ConfigurationChanged());

                        }

                        return Result<ArcanumSettings>.Success(resetCandidate.Value.Settings);

                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (write.IsFailure)
            {

                return Result<ConfigurationPresetResetCommitResult>.Failure(
                    await HandleConfigurationWriteFailureAsync(
                            journal,
                            write.Error,
                            CancellationToken.None)
                        .ConfigureAwait(false));

            }

            configurationWritten = true;

            if (hooks?.ContinueAfterConfigurationWrite?.Invoke() == false)
            {

                Error interrupted = new(
                    "Preset.ResetFailed",
                    "Preset reset was interrupted after the configuration write.");

                Error rolledBack = await RollBackPreparedAsync(
                        journal,
                        interrupted,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested
                    && IsRollbackFailure(rolledBack))
                {

                    return Result<ConfigurationPresetResetCommitResult>.Failure(rolledBack);

                }

                journal = null;

                cancellationToken.ThrowIfCancellationRequested();

                return Result<ConfigurationPresetResetCommitResult>.Failure(rolledBack);

            }

            ArcanumSettings verified = ConfigurationBootstrapper.LoadPersistedArcanumSettings();

            if (!ValuesMatch(verified, journal.CandidateValues))
            {

                return Result<ConfigurationPresetResetCommitResult>.Failure(
                    await RollBackPreparedAsync(
                            journal,
                            new Error(
                                "Preset.VerificationFailed",
                                "Reset configuration verification did not match the candidate snapshot."),
                            CancellationToken.None)
                        .ConfigureAwait(false));

            }

            TryDeleteKnownFile(ArcanumPaths.ConfigurationPresetStateFile);

            TryDeleteKnownFile(ArcanumPaths.ConfigurationPresetRollbackFile);

            TryDeleteKnownFile(ArcanumPaths.ConfigurationPresetJournalFile);

            journal = null;

            Result<ConfigurationPresetSnapshot> snapshot =
                await ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);

            return snapshot.IsSuccess
                ? Result<ConfigurationPresetResetCommitResult>.Success(
                    new ConfigurationPresetResetCommitResult(
                        ConfigurationPresetRollbackStatus.Restored,
                        snapshot.Value,
                        resetCandidate.Value.RestoredSettingCount,
                        resetCandidate.Value.PreservedDriftCount))
                : Result<ConfigurationPresetResetCommitResult>.Failure(snapshot.Error);

        }
        catch (OperationCanceledException)
        {

            Error cancelled = new(
                "Preset.ResetCancelled",
                "Preset reset was cancelled.");

            if (journal is not null && configurationWritten)
            {

                Error rolledBack = await RollBackPreparedAsync(
                        journal,
                        cancelled,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                if (IsRollbackFailure(rolledBack))
                {

                    return Result<ConfigurationPresetResetCommitResult>.Failure(rolledBack);

                }

            }
            else if (journal is not null)
            {

                TryDeleteKnownFile(ArcanumPaths.ConfigurationPresetJournalFile);

            }

            throw;

        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {

            logger.LogError(exception, "Preset reset transaction failed.");

            Error failure = new("Preset.ResetFailed", exception.Message);

            if (journal is not null && configurationWritten)
            {

                failure = await RollBackPreparedAsync(
                        journal,
                        failure,
                        CancellationToken.None)
                    .ConfigureAwait(false);

            }
            else if (journal is not null)
            {

                TryDeleteKnownFile(ArcanumPaths.ConfigurationPresetJournalFile);

            }

            return Result<ConfigurationPresetResetCommitResult>.Failure(failure);

        }

    }

    private async Task<Result<ConfigurationPresetSnapshot>> ReadSnapshotAsync(
        CancellationToken cancellationToken)
    {

        ArcanumSettings persisted = ConfigurationBootstrapper.LoadPersistedArcanumSettings();

        ConfigurationPresetProvenance? normalizedProvenance = null;

        Result<ConfigurationPresetProvenance?> state = await ReadProvenanceAsync(
                ArcanumPaths.ConfigurationPresetStateFile,
                optional: true,
                cancellationToken)
            .ConfigureAwait(false);

        if (state.IsFailure)
        {

            return Result<ConfigurationPresetSnapshot>.Failure(state.Error);

        }

        if (state.Value is not null)
        {

            Result stateValidation = ValidateStoredProvenance(state.Value);

            if (stateValidation.IsFailure)
            {

                return Result<ConfigurationPresetSnapshot>.Failure(stateValidation.Error);

            }

            Result<ConfigurationPresetProvenance?> rollback = await ReadProvenanceAsync(
                    ArcanumPaths.ConfigurationPresetRollbackFile,
                    optional: false,
                    cancellationToken)
                .ConfigureAwait(false);

            if (rollback.IsFailure
                || rollback.Value is null
                || ValidateStoredProvenance(rollback.Value).IsFailure
                || !ProvenanceEquals(state.Value, rollback.Value))
            {

                return Result<ConfigurationPresetSnapshot>.Failure(
                    new Error(
                        "Preset.RollbackSnapshotInvalid",
                        "The active preset rollback snapshot is invalid or does not exactly match its provenance."));

            }

            Result<ConfigurationPresetProvenance> normalized =
                NormalizeStoredProvenance(state.Value);

            if (normalized.IsFailure)
            {

                return Result<ConfigurationPresetSnapshot>.Failure(normalized.Error);

            }

            normalizedProvenance = normalized.Value;

        }
        else
        {

            Result<ConfigurationPresetProvenance?> rollback = await ReadProvenanceAsync(
                    ArcanumPaths.ConfigurationPresetRollbackFile,
                    optional: true,
                    cancellationToken)
                .ConfigureAwait(false);

            if (rollback.IsFailure || rollback.Value is not null)
            {

                return Result<ConfigurationPresetSnapshot>.Failure(
                    new Error(
                        "Preset.RollbackSnapshotInvalid",
                        "Preset rollback state exists without matching active provenance."));

            }

        }

        ConfigurationEnvironmentSnapshot environment =
            ConfigurationEnvironmentResolver.Resolve(persisted);

        return Result<ConfigurationPresetSnapshot>.Success(
            new ConfigurationPresetSnapshot(persisted, environment, normalizedProvenance));

    }

    private async Task<Result> PrepareApplyAsync(
        ConfigurationPresetJournalDocument journal,
        ConfigurationPresetProvenance provenance,
        CancellationToken cancellationToken)
    {

        Result journalWrite = await WriteJournalAsync(journal, cancellationToken)
            .ConfigureAwait(false);

        if (journalWrite.IsFailure)
        {

            return journalWrite;

        }

        Result rollbackWrite = await WriteProvenanceAsync(
                ArcanumPaths.ConfigurationPresetRollbackFile,
                provenance,
                cancellationToken)
            .ConfigureAwait(false);

        if (rollbackWrite.IsSuccess)
        {

            return Result.Success();

        }

        Result restored = await RestoreSidecarsAndDeleteJournalAsync(
                journal.PreviousProvenance,
                CancellationToken.None)
            .ConfigureAwait(false);

        return restored.IsSuccess ? rollbackWrite : restored;

    }

    private async Task<Result> ValidateCandidateAsync(
        ArcanumSettings candidate,
        CancellationToken cancellationToken)
    {

        Result outbound = await OutboundUrlGuard
            .ValidateArcanumSettingsAsync(candidate, cancellationToken)
            .ConfigureAwait(false);

        return outbound.IsFailure ? outbound : validator.Validate(candidate);

    }

    private static Result<ResetCandidate> BuildResetCandidate(
        ArcanumSettings current,
        ConfigurationPresetProvenance provenance)
    {

        Dictionary<string, string> applied = provenance.AppliedValues.ToDictionary(
            static value => value.Path,
            static value => value.CanonicalJson,
            StringComparer.OrdinalIgnoreCase);

        ArcanumSettings candidate = ConfigurationPathAccessor.Clone(current);

        int restored = 0;

        int preserved = 0;

        foreach (ConfigurationPresetBaselineValue baseline in provenance.BaselineValues)
        {

            if (!applied.TryGetValue(baseline.Path, out string? appliedValue))
            {

                return InvalidReset($"Rollback snapshot is missing applied value '{baseline.Path}'.");

            }

            string currentValue = ConfigurationPathAccessor.GetCanonicalValue(
                candidate,
                baseline.Path);

            if (!CanonicalEquals(currentValue, appliedValue))
            {

                preserved++;

                continue;

            }

            ConfigurationPathUpdate update = ConfigurationPathAccessor.SetCanonicalValue(
                candidate,
                baseline.Path,
                baseline.CanonicalJson);

            if (!update.IsSuccess)
            {

                return InvalidReset(update.Error!);

            }

            candidate = update.Settings!;

            restored++;

        }

        return Result<ResetCandidate>.Success(
            new ResetCandidate(candidate, restored, preserved));

    }

    private async Task<Error> RollBackPreparedAsync(
        ConfigurationPresetJournalDocument journal,
        Error originalError,
        CancellationToken cleanupToken)
    {

        Result configurationRestore = await RestoreOwnedValuesAsync(journal, cleanupToken)
            .ConfigureAwait(false);

        Result sidecarRestore = await RestoreSidecarsAsync(
                journal.PreviousProvenance,
                cleanupToken)
            .ConfigureAwait(false);

        if (configurationRestore.IsSuccess && sidecarRestore.IsSuccess)
        {

            TryDeleteKnownFile(ArcanumPaths.ConfigurationPresetJournalFile);

            string operation = journal.Operation == "reset" ? "Reset" : "Apply";

            return new Error(
                $"Preset.{operation}Failed.RolledBack",
                $"{originalError.Message} Preset-owned values were restored and concurrent user changes were preserved.");

        }

        return RollbackFailed(journal.Operation, originalError);

    }

    private async Task<Error> HandleConfigurationWriteFailureAsync(
        ConfigurationPresetJournalDocument journal,
        Error writeError,
        CancellationToken cleanupToken)
    {

        if (string.Equals(
                writeError.Code,
                "Configuration.WriteFailed.Unverified",
                StringComparison.Ordinal))
        {

            return await RollBackPreparedAsync(journal, writeError, cleanupToken)
                .ConfigureAwait(false);

        }

        Result cleanup = await RestoreSidecarsAndDeleteJournalAsync(
                journal.PreviousProvenance,
                cleanupToken)
            .ConfigureAwait(false);

        return cleanup.IsSuccess
            ? writeError
            : RollbackFailed(journal.Operation, writeError);

    }

    private async Task<Result> RecoverPreparedTransactionAsync(
        CancellationToken cancellationToken)
    {

        if (!File.Exists(ArcanumPaths.ConfigurationPresetJournalFile))
        {

            return Result.Success();

        }

        Result<ConfigurationPresetJournalDocument?> journalResult =
            await ReadJournalAsync(cancellationToken).ConfigureAwait(false);

        if (journalResult.IsFailure || journalResult.Value is null)
        {

            return Result.Failure(
                journalResult.IsFailure
                    ? journalResult.Error
                    : new Error(
                        "Preset.RecoveryFailed",
                        "The preset transaction journal was empty."));

        }

        ConfigurationPresetJournalDocument journal = journalResult.Value;

        Result journalValidation = ValidateJournal(journal);

        if (journalValidation.IsFailure)
        {

            return journalValidation;

        }

        Result<ConfigurationPresetProvenance?> state = await ReadProvenanceAsync(
                ArcanumPaths.ConfigurationPresetStateFile,
                optional: true,
                cancellationToken)
            .ConfigureAwait(false);

        if (state.IsFailure)
        {

            return Result.Failure(state.Error);

        }

        if (TransactionIsCommitted(journal, state.Value))
        {

            Result completed = await CompleteCommittedSidecarsAsync(
                    journal,
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (completed.IsFailure)
            {

                return completed;

            }

            TryDeleteKnownFile(ArcanumPaths.ConfigurationPresetJournalFile);

            return Result.Success();

        }

        Result configurationRestore = await RestoreOwnedValuesAsync(
                journal,
                CancellationToken.None,
                allowValidatedLegacyRetiredPaths: true)
            .ConfigureAwait(false);

        Result sidecarRestore = await RestoreSidecarsAsync(
                journal.PreviousProvenance,
                CancellationToken.None)
            .ConfigureAwait(false);

        if (configurationRestore.IsFailure || sidecarRestore.IsFailure)
        {

            return Result.Failure(
                new Error(
                    "Preset.RecoveryFailed",
                    "The interrupted preset transaction could not restore its owned values and provenance."));

        }

        TryDeleteKnownFile(ArcanumPaths.ConfigurationPresetJournalFile);

        return Result.Success();

    }

    private async Task<Result> RestoreOwnedValuesAsync(
        ConfigurationPresetJournalDocument journal,
        CancellationToken cancellationToken,
        bool allowValidatedLegacyRetiredPaths = false)
    {

        ArcanumSettings current = ConfigurationBootstrapper.LoadPersistedArcanumSettings();

        Result<ArcanumSettings> merged = BuildConditionalRestore(
            current,
            journal,
            allowValidatedLegacyRetiredPaths);

        if (merged.IsFailure)
        {

            return Result.Failure(merged.Error);

        }

        string expectedCurrentHash = ConfigurationPresetHash.ComputeSettings(current);

        if (MatchesExpectedSettings(merged.Value, expectedCurrentHash))
        {

            return Result.Success();

        }

        Result<ArcanumSettings> write = await writer.UpdateAsync(
                current,
                latest =>
                {

                    if (!MatchesExpectedSettings(latest, expectedCurrentHash))
                    {

                        return Result<ArcanumSettings>.Failure(ConfigurationChanged());

                    }

                    return Result<ArcanumSettings>.Success(merged.Value);

                },
                cancellationToken)
            .ConfigureAwait(false);

        return write.IsSuccess ? Result.Success() : Result.Failure(write.Error);

    }

    private static Result<ArcanumSettings> BuildConditionalRestore(
        ArcanumSettings current,
        ConfigurationPresetJournalDocument journal,
        bool allowValidatedLegacyRetiredPaths)
    {

        Dictionary<string, string> candidate = journal.CandidateValues.ToDictionary(
            static value => value.Path,
            static value => value.CanonicalJson,
            StringComparer.OrdinalIgnoreCase);

        ArcanumSettings restored = ConfigurationPathAccessor.Clone(current);

        ConfigurationPresetProvenance? owner = journal.Operation == "apply"
            ? journal.NextProvenance
            : journal.PreviousProvenance;

        bool skipLegacyRetiredPaths = allowValidatedLegacyRetiredPaths
            && owner is not null
            && IsAffectedLegacyProvenance(owner);

        foreach (ConfigurationPresetBaselineValue previous in journal.PreviousValues)
        {

            if (!candidate.TryGetValue(previous.Path, out string? candidateValue))
            {

                return Result<ArcanumSettings>.Failure(InvalidJournalError());

            }

            if (skipLegacyRetiredPaths && IsRetiredWardApprovalPath(previous.Path))
            {

                continue;

            }

            string currentValue = ConfigurationPathAccessor.GetCanonicalValue(
                restored,
                previous.Path);

            if (!CanonicalEquals(currentValue, candidateValue))
            {

                continue;

            }

            ConfigurationPathUpdate update = ConfigurationPathAccessor.SetCanonicalValue(
                restored,
                previous.Path,
                previous.CanonicalJson);

            if (!update.IsSuccess)
            {

                return Result<ArcanumSettings>.Failure(
                    new Error("Preset.RecoveryFailed", update.Error!));

            }

            restored = update.Settings!;

        }

        return Result<ArcanumSettings>.Success(restored);

    }

    private static ConfigurationPresetJournalDocument CreateJournal(
        string operation,
        ArcanumSettings previous,
        ArcanumSettings candidate,
        IEnumerable<string> paths,
        ConfigurationPresetProvenance? previousProvenance,
        ConfigurationPresetProvenance? nextProvenance)
    {

        string[] ownedPaths = paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        ImmutableArray<ConfigurationPresetBaselineValue> previousValues = Values(
            previous,
            ownedPaths);

        ImmutableArray<ConfigurationPresetBaselineValue> candidateValues = Values(
            candidate,
            ownedPaths);

        return new ConfigurationPresetJournalDocument(
            operation,
            previousValues,
            candidateValues,
            ConfigurationPresetHash.ComputeCanonicalValues(previousValues),
            ConfigurationPresetHash.ComputeCanonicalValues(candidateValues),
            previousProvenance,
            nextProvenance,
            DateTimeOffset.UtcNow);

    }

    private static ImmutableArray<ConfigurationPresetBaselineValue> Values(
        ArcanumSettings settings,
        IEnumerable<string> paths) =>
        [
            .. paths.Select(path => new ConfigurationPresetBaselineValue(
                path,
                ConfigurationPathAccessor.GetCanonicalValue(settings, path))),
        ];

    private static Result ValidateApplyProvenance(
        ConfigurationPresetProvenance provenance,
        ArcanumSettings current,
        ArcanumSettings candidate)
    {

        Result stored = ValidateStoredProvenance(provenance);

        if (stored.IsFailure)
        {

            return stored;

        }

        foreach (ConfigurationPresetBaselineValue baseline in provenance.BaselineValues)
        {

            if (!CanonicalEquals(
                    baseline.CanonicalJson,
                    ConfigurationPathAccessor.GetCanonicalValue(current, baseline.Path)))
            {

                return Result.Failure(
                    new Error(
                        "Preset.RollbackSnapshotInvalid",
                        $"Preset baseline '{baseline.Path}' does not match the current configuration."));

            }

        }

        foreach (ConfigurationPresetBaselineValue applied in provenance.AppliedValues)
        {

            if (!CanonicalEquals(
                    applied.CanonicalJson,
                    ConfigurationPathAccessor.GetCanonicalValue(candidate, applied.Path)))
            {

                return Result.Failure(
                    new Error(
                        "Preset.RollbackSnapshotInvalid",
                        $"Preset applied value '{applied.Path}' does not match the candidate configuration."));

            }

        }

        ArcanumSettings expectedCandidate = ConfigurationPathAccessor.Clone(current);

        foreach (ConfigurationPresetBaselineValue applied in provenance.AppliedValues)
        {

            ConfigurationPathUpdate update = ConfigurationPathAccessor.SetCanonicalValue(
                expectedCandidate,
                applied.Path,
                applied.CanonicalJson);

            if (!update.IsSuccess)
            {

                return Result.Failure(
                    new Error("Preset.CandidateInvalid", update.Error!));

            }

            expectedCandidate = update.Settings!;

        }

        if (!CanonicalEquals(
                ConfigurationPresetHash.ComputeSettings(expectedCandidate),
                ConfigurationPresetHash.ComputeSettings(candidate)))
        {

            return Result.Failure(
                new Error(
                    "Preset.CandidateInvalid",
                    "The supplied preset candidate changed values outside the preset ownership set."));

        }

        return Result.Success();

    }

    private static Result ValidateStoredProvenance(
        ConfigurationPresetProvenance provenance)
    {

        if (!ProvenanceShapeIsValid(provenance))
        {

            return Result.Failure(
                new Error(
                    "Preset.RollbackSnapshotInvalid",
                    "Preset provenance is missing required values."));

        }

        ConfigurationPresetDefinition? definition = ConfigurationPresetCatalog.FindVersion(
            provenance.PresetId,
            provenance.Version);

        if (definition is null
            || !string.Equals(definition.Id, provenance.PresetId, StringComparison.Ordinal)
            || definition.Version != provenance.Version)
        {

            return Result.Failure(
                new Error(
                    "Preset.RollbackSnapshotInvalid",
                    "Preset provenance does not reference a known immutable preset version."));

        }

        if (!PathsExactlyMatch(definition, provenance.BaselineValues)
            || !PathsExactlyMatch(definition, provenance.AppliedValues))
        {

            return Result.Failure(
                new Error(
                    "Preset.RollbackSnapshotInvalid",
                    "Preset provenance paths do not exactly match the versioned preset ownership set."));

        }

        Dictionary<string, ConfigurationPresetOwnedSetting> owned = definition.OwnedSettings
            .ToDictionary(
                static setting => setting.Path,
                StringComparer.OrdinalIgnoreCase);

        foreach (ConfigurationPresetBaselineValue applied in provenance.AppliedValues)
        {

            if (!CanonicalEquals(
                    owned[applied.Path].CanonicalJson,
                    applied.CanonicalJson))
            {

                return Result.Failure(
                    new Error(
                        "Preset.RollbackSnapshotInvalid",
                        $"Preset applied value '{applied.Path}' does not match its immutable definition."));

            }

        }

        foreach (ConfigurationPresetBaselineValue baseline in provenance.BaselineValues)
        {

            if (IsAffectedLegacyDefinition(definition)
                && IsRetiredWardApprovalPath(baseline.Path))
            {

                if (!IsCanonicalBoolean(baseline.CanonicalJson))
                {

                    return Result.Failure(
                        new Error(
                            "Preset.RollbackSnapshotInvalid",
                            $"Preset baseline '{baseline.Path}' is not an exact canonical boolean."));

                }

                continue;

            }

            ConfigurationPathUpdate parsed = ConfigurationPathAccessor.SetCanonicalValue(
                new ArcanumSettings(),
                baseline.Path,
                baseline.CanonicalJson);

            if (!parsed.IsSuccess)
            {

                return Result.Failure(
                    new Error("Preset.RollbackSnapshotInvalid", parsed.Error!));

            }

        }

        string expectedHash = ConfigurationPresetHash.ComputeCanonicalValues(
            provenance.AppliedValues);

        return CanonicalEquals(expectedHash, provenance.OwnedValuesHash)
            ? Result.Success()
            : Result.Failure(
                new Error(
                    "Preset.RollbackSnapshotInvalid",
                    "Preset provenance owned-values hash is invalid."));

    }

    private static Result<ConfigurationPresetProvenance> NormalizeStoredProvenance(
        ConfigurationPresetProvenance provenance)
    {

        if (!IsAffectedLegacyProvenance(provenance))
        {

            return Result<ConfigurationPresetProvenance>.Success(provenance);

        }

        ConfigurationPresetDefinition current = ConfigurationPresetCatalog.FindVersion(
            provenance.PresetId,
            2)!;

        Dictionary<string, string> baselines = provenance.BaselineValues.ToDictionary(
            static value => value.Path,
            static value => value.CanonicalJson,
            StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string> applied = provenance.AppliedValues.ToDictionary(
            static value => value.Path,
            static value => value.CanonicalJson,
            StringComparer.OrdinalIgnoreCase);

        ImmutableArray<ConfigurationPresetBaselineValue> normalizedBaselines =
        [
            .. current.OwnedSettings.Select(setting =>
                new ConfigurationPresetBaselineValue(
                    setting.Path,
                    baselines[setting.Path])),
        ];

        ImmutableArray<ConfigurationPresetBaselineValue> normalizedApplied =
        [
            .. current.OwnedSettings.Select(setting =>
                new ConfigurationPresetBaselineValue(
                    setting.Path,
                    applied[setting.Path])),
        ];

        ConfigurationPresetProvenance normalized = provenance with
        {

            Version = 2,

            OwnedValuesHash = ConfigurationPresetHash.ComputeCanonicalValues(
                normalizedApplied),

            BaselineValues = normalizedBaselines,

            AppliedValues = normalizedApplied,

        };

        Result validation = ValidateStoredProvenance(normalized);

        return validation.IsSuccess
            ? Result<ConfigurationPresetProvenance>.Success(normalized)
            : Result<ConfigurationPresetProvenance>.Failure(validation.Error);

    }

    private static bool IsAffectedLegacyProvenance(
        ConfigurationPresetProvenance provenance)
    {

        ConfigurationPresetDefinition? definition = ConfigurationPresetCatalog.FindVersion(
            provenance.PresetId,
            provenance.Version);

        return definition is not null && IsAffectedLegacyDefinition(definition);

    }

    private static bool IsAffectedLegacyDefinition(
        ConfigurationPresetDefinition definition) =>
        definition.Version == 1
        && definition.Id is "general-assistant"
            or "coding-workspace"
            or "research"
            or "private-offline"
            or "automation";

    private static bool IsRetiredWardApprovalPath(string path) =>
        string.Equals(path, "security.ward.enabled", StringComparison.Ordinal)
        || string.Equals(
            path,
            "security.ward.autoDenyInUnattendedMode",
            StringComparison.Ordinal);

    private static bool IsCanonicalBoolean(string canonicalJson) =>
        string.Equals(canonicalJson, "true", StringComparison.Ordinal)
        || string.Equals(canonicalJson, "false", StringComparison.Ordinal);

    private static Result ValidateJournal(ConfigurationPresetJournalDocument journal)
    {

        if (string.IsNullOrWhiteSpace(journal.Operation)
            || journal.PreparedAt == default
            || string.IsNullOrWhiteSpace(journal.PreviousValuesHash)
            || string.IsNullOrWhiteSpace(journal.CandidateValuesHash)
            || !ValuesAreWellFormed(journal.PreviousValues)
            || !ValuesAreWellFormed(journal.CandidateValues))
        {

            return Result.Failure(InvalidJournalError());

        }

        bool operationShape = journal.Operation switch
        {
            "apply" => journal.NextProvenance is not null,
            "reset" => journal.PreviousProvenance is not null
                && journal.NextProvenance is null,
            _ => false,
        };

        ConfigurationPresetProvenance? owner = journal.Operation == "apply"
            ? journal.NextProvenance
            : journal.PreviousProvenance;

        if (!operationShape
            || owner is null
            || ValidateStoredProvenance(owner).IsFailure
            || !ValuePathsMatch(
                owner.BaselineValues,
                journal.PreviousValues,
                journal.CandidateValues)
            || !CanonicalEquals(
                journal.PreviousValuesHash,
                ConfigurationPresetHash.ComputeCanonicalValues(journal.PreviousValues))
            || !CanonicalEquals(
                journal.CandidateValuesHash,
                ConfigurationPresetHash.ComputeCanonicalValues(journal.CandidateValues)))
        {

            return Result.Failure(InvalidJournalError());

        }

        if (journal.PreviousProvenance is not null
            && ValidateStoredProvenance(journal.PreviousProvenance).IsFailure)
        {

            return Result.Failure(InvalidJournalError());

        }

        if (journal.Operation == "apply"
            && (!ValuesEqual(journal.PreviousValues, owner.BaselineValues)
                || !ValuesEqual(journal.CandidateValues, owner.AppliedValues)))
        {

            return Result.Failure(InvalidJournalError());

        }

        if (journal.Operation == "reset"
            && !ResetJournalValuesAreValid(journal, owner))
        {

            return Result.Failure(InvalidJournalError());

        }

        return Result.Success();

    }

    private static bool ResetJournalValuesAreValid(
        ConfigurationPresetJournalDocument journal,
        ConfigurationPresetProvenance provenance)
    {

        Dictionary<string, string> baseline = provenance.BaselineValues.ToDictionary(
            static value => value.Path,
            static value => value.CanonicalJson,
            StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string> applied = provenance.AppliedValues.ToDictionary(
            static value => value.Path,
            static value => value.CanonicalJson,
            StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string> candidate = journal.CandidateValues.ToDictionary(
            static value => value.Path,
            static value => value.CanonicalJson,
            StringComparer.OrdinalIgnoreCase);

        return journal.PreviousValues.All(previous =>
        {

            string expected = CanonicalEquals(
                previous.CanonicalJson,
                applied[previous.Path])
                ? baseline[previous.Path]
                : previous.CanonicalJson;

            return candidate.TryGetValue(previous.Path, out string? candidateValue)
                && CanonicalEquals(expected, candidateValue);

        });

    }

    private static bool TransactionIsCommitted(
        ConfigurationPresetJournalDocument journal,
        ConfigurationPresetProvenance? state) =>
        journal.Operation == "apply"
            ? state is not null
                && journal.NextProvenance is not null
                && ProvenanceEquals(state, journal.NextProvenance)
            : state is null;

    private static async Task<Result> CompleteCommittedSidecarsAsync(
        ConfigurationPresetJournalDocument journal,
        CancellationToken cancellationToken)
    {

        if (journal.Operation == "reset")
        {

            TryDeleteKnownFile(ArcanumPaths.ConfigurationPresetStateFile);

            TryDeleteKnownFile(ArcanumPaths.ConfigurationPresetRollbackFile);

            return Result.Success();

        }

        ConfigurationPresetProvenance provenance = journal.NextProvenance!;

        Result state = await WriteProvenanceAsync(
                ArcanumPaths.ConfigurationPresetStateFile,
                provenance,
                cancellationToken)
            .ConfigureAwait(false);

        return state.IsFailure
            ? state
            : await WriteProvenanceAsync(
                    ArcanumPaths.ConfigurationPresetRollbackFile,
                    provenance,
                    cancellationToken)
                .ConfigureAwait(false);

    }

    private static async Task<Result> RestoreSidecarsAndDeleteJournalAsync(
        ConfigurationPresetProvenance? provenance,
        CancellationToken cancellationToken)
    {

        Result restored = await RestoreSidecarsAsync(provenance, cancellationToken)
            .ConfigureAwait(false);

        if (restored.IsSuccess)
        {

            TryDeleteKnownFile(ArcanumPaths.ConfigurationPresetJournalFile);

        }

        return restored;

    }

    private static async Task<Result> RestoreSidecarsAsync(
        ConfigurationPresetProvenance? provenance,
        CancellationToken cancellationToken)
    {

        if (provenance is null)
        {

            TryDeleteKnownFile(ArcanumPaths.ConfigurationPresetStateFile);

            TryDeleteKnownFile(ArcanumPaths.ConfigurationPresetRollbackFile);

            return Result.Success();

        }

        Result state = await WriteProvenanceAsync(
                ArcanumPaths.ConfigurationPresetStateFile,
                provenance,
                cancellationToken)
            .ConfigureAwait(false);

        return state.IsFailure
            ? state
            : await WriteProvenanceAsync(
                    ArcanumPaths.ConfigurationPresetRollbackFile,
                    provenance,
                    cancellationToken)
                .ConfigureAwait(false);

    }

    private static Task<Result> WriteJournalAsync(
        ConfigurationPresetJournalDocument journal,
        CancellationToken cancellationToken) =>
        WriteAtomicAsync(
            ArcanumPaths.ConfigurationPresetJournalFile,
            (stream, token) => JsonSerializer.SerializeAsync(
                stream,
                journal,
                ConfigurationPresetPersistenceJsonContext.Default.ConfigurationPresetJournalDocument,
                token),
            cancellationToken);

    private static Task<Result> WriteProvenanceAsync(
        string path,
        ConfigurationPresetProvenance provenance,
        CancellationToken cancellationToken) =>
        WriteAtomicAsync(
            path,
            (stream, token) => JsonSerializer.SerializeAsync(
                stream,
                provenance,
                ConfigurationPresetPersistenceJsonContext.Default.ConfigurationPresetProvenance,
                token),
            cancellationToken);

    private static async Task<Result> WriteAtomicAsync(
        string path,
        Func<Stream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken)
    {

        if (!SecureFilePermissions.TryEnsureOwnerOnlyDirectoryExistsStrict(
                ArcanumPaths.GrimoireDirectory))
        {

            return Result.Failure(
                new Error(
                    "Preset.SidecarWriteFailed",
                    "The preset state directory could not be verified as owner-only."));

        }

        string tempPath = Path.Combine(
            ArcanumPaths.GrimoireDirectory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
                path,
                tempPath,
                writeAsync,
                cancellationToken,
                beforeReplace: () =>
                    SecureFilePermissions.TryApplyOwnerOnlyFileStrict(tempPath),
                afterReplace: () =>
                    SecureFilePermissions.TryApplyOwnerOnlyFileStrict(path))
            .ConfigureAwait(false);

        return status == AtomicReplaceStatus.Succeeded
            ? Result.Success()
            : Result.Failure(
                new Error(
                    "Preset.SidecarWriteFailed",
                    $"Atomic preset sidecar replacement did not succeed ({status})."));

    }

    private static async Task<Result<ConfigurationPresetProvenance?>> ReadProvenanceAsync(
        string path,
        bool optional,
        CancellationToken cancellationToken)
    {

        using SecureFileReadResult read = await SecureFileReader.ReadBytesAsync(
                path,
                MaxSidecarBytes,
                cancellationToken)
            .ConfigureAwait(false);

        if (read.Status == SecureFileReadStatus.NotFound)
        {

            return optional
                ? Result<ConfigurationPresetProvenance?>.Success(null)
                : Result<ConfigurationPresetProvenance?>.Failure(
                    new Error(
                        "Preset.RollbackSnapshotInvalid",
                        $"Required preset sidecar '{Path.GetFileName(path)}' is missing."));

        }

        if (read.Status != SecureFileReadStatus.Success)
        {

            return Result<ConfigurationPresetProvenance?>.Failure(
                new Error(
                    "Preset.SidecarInvalid",
                    $"Preset sidecar '{Path.GetFileName(path)}' was rejected ({read.Status})."));

        }

        try
        {

            ConfigurationPresetProvenance? provenance = JsonSerializer.Deserialize(
                read.Bytes.Span,
                ConfigurationPresetPersistenceJsonContext.Default.ConfigurationPresetProvenance);

            return provenance is null || !ProvenanceShapeIsValid(provenance)
                ? Result<ConfigurationPresetProvenance?>.Failure(
                    new Error(
                        "Preset.SidecarInvalid",
                        $"Preset sidecar '{Path.GetFileName(path)}' was empty or missing required values."))
                : Result<ConfigurationPresetProvenance?>.Success(provenance);

        }
        catch (JsonException exception)
        {

            return Result<ConfigurationPresetProvenance?>.Failure(
                new Error("Preset.SidecarInvalid", exception.Message));

        }

    }

    private static async Task<Result<ConfigurationPresetJournalDocument?>> ReadJournalAsync(
        CancellationToken cancellationToken)
    {

        using SecureFileReadResult read = await SecureFileReader.ReadBytesAsync(
                ArcanumPaths.ConfigurationPresetJournalFile,
                MaxSidecarBytes,
                cancellationToken)
            .ConfigureAwait(false);

        if (read.Status != SecureFileReadStatus.Success)
        {

            return Result<ConfigurationPresetJournalDocument?>.Failure(
                new Error(
                    "Preset.RecoveryFailed",
                    $"Preset transaction journal was rejected ({read.Status})."));

        }

        try
        {

            ConfigurationPresetJournalDocument? journal = JsonSerializer.Deserialize(
                read.Bytes.Span,
                ConfigurationPresetPersistenceJsonContext.Default.ConfigurationPresetJournalDocument);

            return Result<ConfigurationPresetJournalDocument?>.Success(journal);

        }
        catch (JsonException exception)
        {

            return Result<ConfigurationPresetJournalDocument?>.Failure(
                new Error("Preset.RecoveryFailed", exception.Message));

        }

    }

    private static bool PathsExactlyMatch(
        ConfigurationPresetDefinition definition,
        ImmutableArray<ConfigurationPresetBaselineValue> values)
    {

        if (values.IsDefault || values.Length != definition.OwnedSettings.Length)
        {

            return false;

        }

        HashSet<string> paths = values
            .Select(static value => value.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return paths.Count == values.Length
            && definition.OwnedSettings.All(setting => paths.Contains(setting.Path));

    }

    private static bool ProvenanceShapeIsValid(ConfigurationPresetProvenance provenance) =>
        !string.IsNullOrWhiteSpace(provenance.PresetId)
        && provenance.Version > 0
        && provenance.AppliedAt != default
        && !string.IsNullOrWhiteSpace(provenance.OwnedValuesHash)
        && ValuesAreWellFormed(provenance.BaselineValues)
        && ValuesAreWellFormed(provenance.AppliedValues);

    private static bool ValuesAreWellFormed(
        ImmutableArray<ConfigurationPresetBaselineValue> values) =>
        !values.IsDefault
        && values.All(static value =>
            value is not null
            && !string.IsNullOrWhiteSpace(value.Path)
            && !string.IsNullOrWhiteSpace(value.CanonicalJson));

    private static bool ValuePathsMatch(
        ImmutableArray<ConfigurationPresetBaselineValue> owner,
        ImmutableArray<ConfigurationPresetBaselineValue> previous,
        ImmutableArray<ConfigurationPresetBaselineValue> candidate)
    {

        if (owner.IsDefault || previous.IsDefault || candidate.IsDefault
            || owner.Length != previous.Length
            || owner.Length != candidate.Length)
        {

            return false;

        }

        HashSet<string> ownerPaths = owner
            .Select(static value => value.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<string> previousPaths = previous
            .Select(static value => value.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<string> candidatePaths = candidate
            .Select(static value => value.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return ownerPaths.Count == owner.Length
            && previousPaths.Count == previous.Length
            && candidatePaths.Count == candidate.Length
            && ownerPaths.SetEquals(previousPaths)
            && ownerPaths.SetEquals(candidatePaths);

    }

    private static bool ValuesMatch(
        ArcanumSettings settings,
        ImmutableArray<ConfigurationPresetBaselineValue> values) =>
        values.All(value => CanonicalEquals(
            ConfigurationPathAccessor.GetCanonicalValue(settings, value.Path),
            value.CanonicalJson));

    private static bool ValuesEqual(
        ImmutableArray<ConfigurationPresetBaselineValue> first,
        ImmutableArray<ConfigurationPresetBaselineValue> second)
    {

        if (first.Length != second.Length)
        {

            return false;

        }

        Dictionary<string, string> expected = first.ToDictionary(
            static value => value.Path,
            static value => value.CanonicalJson,
            StringComparer.OrdinalIgnoreCase);

        return second.All(value =>
            expected.TryGetValue(value.Path, out string? canonical)
            && CanonicalEquals(canonical, value.CanonicalJson));

    }

    private static bool ProvenanceEquals(
        ConfigurationPresetProvenance first,
        ConfigurationPresetProvenance second) =>
        string.Equals(first.PresetId, second.PresetId, StringComparison.Ordinal)
        && first.Version == second.Version
        && first.AppliedAt == second.AppliedAt
        && CanonicalEquals(first.OwnedValuesHash, second.OwnedValuesHash)
        && first.BaselineValues.SequenceEqual(second.BaselineValues)
        && first.AppliedValues.SequenceEqual(second.AppliedValues);

    private static bool MatchesExpectedSettings(
        ArcanumSettings settings,
        string expectedHash) =>
        CanonicalEquals(
            ConfigurationPresetHash.ComputeSettings(settings),
            expectedHash);

    private static bool CanonicalEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static Result<ResetCandidate> InvalidReset(string detail) =>
        Result<ResetCandidate>.Failure(
            new Error("Preset.RollbackSnapshotInvalid", detail));

    private static Error InvalidJournalError() =>
        new(
            "Preset.RecoveryFailed",
            "The preset transaction journal is invalid or does not match a versioned preset.");

    private static Error ConfigurationChanged() =>
        new(
            "Preset.ConfigurationChanged",
            "arcanum.json changed after the preset plan was created. Run preset diff again.");

    private static Error RollbackFailed(string operation, Error originalError)
    {

        string label = operation == "reset" ? "Reset" : "Apply";

        return new Error(
            $"Preset.{label}Failed.RollbackFailed",
            $"{originalError.Message} Automatic owner-path rollback did not complete; inspect arcanum.json and the preset journal before retrying.");

    }

    private static bool IsRollbackFailure(Error error) =>
        error.Code.EndsWith(".RollbackFailed", StringComparison.Ordinal);

    /// <summary>
    /// Removes a known sidecar or journal file. Cleanup runs from cancellation and rollback
    /// handlers, so a denied or locked delete is reported rather than thrown: replacing the
    /// in-flight failure would lose the original outcome and skip the pending rethrow.
    /// </summary>
    internal static bool TryDeleteKnownFile(string path)
    {

        try
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

            return true;

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            return false;

        }

    }

    private static bool IsExpectedFileFailure(Exception exception) =>
        exception is IOException
        or UnauthorizedAccessException
        or JsonException
        or InvalidOperationException;

    private sealed record ResetCandidate(
        ArcanumSettings Settings,
        int RestoredSettingCount,
        int PreservedDriftCount);

}
