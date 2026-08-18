using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed class DataRetentionPolicyStore : IDataRetentionPolicyStore
{

    private readonly ConfigurationWriter _writer;

    private readonly SemaphoreSlim _updateLock = new(1, 1);

    private ArcanumSettings _settings;

    private RetentionSettings _current;

    public DataRetentionPolicyStore(
        IOptionsMonitor<ArcanumSettings> options,
        ConfigurationWriter writer)
    {

        _writer = writer;

        ArcanumSettings initial = writer.Latest ?? options.CurrentValue;

        _current = Normalize(
            initial.Retention
            ?? new RetentionSettings());

        _settings = initial with
        {

            Retention = _current,

        };

    }

    public RetentionSettings Current
    {

        get
        {

            ArcanumSettings? latest = _writer.Latest;

            if (latest is not null)
            {

                RetentionSettings refreshed = Normalize(
                    latest.Retention
                    ?? new RetentionSettings());

                Volatile.Write(ref _current, refreshed);

                Volatile.Write(ref _settings, latest);

                return Normalize(refreshed);

            }

            return Normalize(Volatile.Read(ref _current));

        }

    }

    public async Task<Result<RetentionSettings>> UpdateRuleAsync(
        RetentionRuleUpdateRequest request,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        if (request.Enabled && request.Days is null)
        {

            return Result<RetentionSettings>.Failure(
                new Error(
                    ErrorCodes.Data.InvalidRequest,
                    "Retention days are required when enabling a rule."));

        }

        await _updateLock
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {

            Result<ArcanumSettings> write = await _writer
                .UpdateAsync(
                    Volatile.Read(ref _settings),
                    currentSettings =>
                    {

                        RetentionSettings current = Normalize(
                            currentSettings.Retention
                            ?? new RetentionSettings());

                        if (!TryUpdateRule(
                                current,
                                request,
                                out RetentionSettings updated,
                                out string refusal))
                        {

                            return Result<ArcanumSettings>.Failure(
                                new Error(ErrorCodes.Data.InvalidRequest, refusal));

                        }

                        return Result<ArcanumSettings>.Success(
                            currentSettings with { Retention = updated });

                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (write.IsFailure)
            {

                return Result<RetentionSettings>.Failure(write.Error);

            }

            Volatile.Write(ref _settings, write.Value);

            RetentionSettings published = Normalize(
                write.Value.Retention
                ?? new RetentionSettings());

            Volatile.Write(ref _current, published);

            return Result<RetentionSettings>.Success(Normalize(published));

        }
        finally
        {

            _updateLock.Release();

        }

    }

    /// <summary>
    /// Applies one rule update, or names exactly why it could not be applied.
    /// </summary>
    /// <remarks>
    /// The two refusals are separated because they mean opposite things to an operator. An unrecognized
    /// name is a typo they should fix. A recognized class with no rule — <c>covenant</c> is the only one
    /// — is a deliberate design property, and telling them it "is not recognized" would send them
    /// hunting for a spelling mistake that does not exist (§10.20.1).
    /// </remarks>
    private static bool TryUpdateRule(
        RetentionSettings current,
        RetentionRuleUpdateRequest request,
        out RetentionSettings updated,
        out string refusal)
    {

        if (!TryParseDataClass(request.DataClass, out RetentionDataClass dataClass))
        {

            updated = current;

            refusal = "The retention data class is not recognized.";

            return false;

        }

        RetentionRuleSettings? existing =
            DataRetentionSettingsCatalog.ResolveRule(current, dataClass);

        if (existing is null)
        {

            updated = current;

            refusal = dataClass is RetentionDataClass.Covenant
                ? "The Covenant has no time-based retention rule and cannot be given one. It is "
                    + "inventoried by 'data status' and removed only by an explicit reset."
                : "The retention data class has no configurable rule.";

            return false;

        }

        RetentionRuleSettings replacement = new()
        {

            Enabled = request.Enabled,

            Days = ArcanumSettingClamps.RetentionRuleDays(
                request.Enabled
                    ? request.Days!.Value
                    : existing.Days),

        };

        updated = current with { };

        switch (dataClass)
        {

            case RetentionDataClass.ActiveSessions:
                updated.ActiveSessions = replacement;
                break;

            case RetentionDataClass.ArchivedSessions:
                updated.ArchivedSessions = replacement;
                break;

            case RetentionDataClass.Entries:
                updated.Entries = replacement;
                break;

            case RetentionDataClass.AttachmentVersions:
            case RetentionDataClass.AttachmentBytes:
            case RetentionDataClass.AttachmentChunks:
            case RetentionDataClass.AttachmentEmbeddings:
                updated.Attachments = replacement;
                break;

            case RetentionDataClass.UploadedFiles:
            case RetentionDataClass.BatchInputFiles:
            case RetentionDataClass.BatchOutputFiles:
            case RetentionDataClass.BatchErrorFiles:
                updated.UploadedFiles = replacement;
                break;

            case RetentionDataClass.CompletedBatches:
                updated.CompletedBatches = replacement;
                break;

            case RetentionDataClass.SagaMemories:
                updated.SagaMemories = replacement;
                break;

            case RetentionDataClass.LexiconEntries:
                updated.LexiconEntries = replacement;
                break;

            case RetentionDataClass.WorkspaceChunks:
            case RetentionDataClass.WorkspaceEmbeddings:
                updated.WorkspaceIndexes = replacement;
                break;

            case RetentionDataClass.SessionEntryEmbeddings:
                updated.SessionEntryEmbeddings = replacement;
                break;

            case RetentionDataClass.AuditLogs:
                updated.AuditLogs = replacement;
                break;

            case RetentionDataClass.GuardrailLogs:
                updated.GuardrailLogs = replacement;
                break;

            case RetentionDataClass.IdempotencyClaims:
                updated.IdempotencyClaims = replacement;
                break;

            case RetentionDataClass.InferenceRuns:
            case RetentionDataClass.BillableOperations:
            case RetentionDataClass.BudgetReservations:
            case RetentionDataClass.CostAdjustments:
                updated.Accounting = replacement;
                break;

            case RetentionDataClass.LongRunningOperations:
                updated.LongRunningOperations = replacement;
                break;

            case RetentionDataClass.SanctumBreaches:
                updated.SanctumBreaches = replacement;
                break;

            case RetentionDataClass.DaemonExecutions:
                updated.DaemonHistory = replacement;
                break;

            default:
                refusal = "The retention data class has no configurable rule.";

                return false;

        }

        updated = Normalize(updated);

        refusal = string.Empty;

        return true;

    }

    private static bool TryParseDataClass(
        string? value,
        out RetentionDataClass dataClass) =>
        DataRetentionDataClassParser.TryParse(value, out dataClass);

    private static RetentionSettings Normalize(RetentionSettings source)
    {

        int accountingMinimum =
            ArcanumSettingClamps.RetentionAccountingMinimumDays(
                source.AccountingMinimumDays);

        RetentionRuleSettings accounting = NormalizeRule(source.Accounting);

        accounting.Days = Math.Max(accounting.Days, accountingMinimum);

        return source with
        {

            SweepIntervalHours =
                ArcanumSettingClamps.RetentionSweepIntervalHours(
                    source.SweepIntervalHours),

            AccountingMinimumDays = accountingMinimum,

            ActiveSessions = NormalizeRule(source.ActiveSessions),

            ArchivedSessions = NormalizeRule(source.ArchivedSessions),

            Entries = NormalizeRule(source.Entries),

            Attachments = NormalizeRule(source.Attachments),

            UploadedFiles = NormalizeRule(source.UploadedFiles),

            CompletedBatches = NormalizeRule(source.CompletedBatches),

            SagaMemories = NormalizeRule(source.SagaMemories),

            LexiconEntries = NormalizeRule(source.LexiconEntries),

            WorkspaceIndexes = NormalizeRule(source.WorkspaceIndexes),

            SessionEntryEmbeddings = NormalizeRule(
                source.SessionEntryEmbeddings),

            AuditLogs = NormalizeRule(source.AuditLogs),

            GuardrailLogs = NormalizeRule(source.GuardrailLogs),

            IdempotencyClaims = NormalizeRule(source.IdempotencyClaims),

            Accounting = accounting,

            LongRunningOperations = NormalizeRule(
                source.LongRunningOperations),

            SanctumBreaches = NormalizeRule(source.SanctumBreaches),

            DaemonHistory = NormalizeRule(source.DaemonHistory),

            ProtectedSessionIds =
                [.. (source.ProtectedSessionIds ?? []).Distinct()],

        };

    }

    private static RetentionRuleSettings NormalizeRule(
        RetentionRuleSettings? rule)
    {

        RetentionRuleSettings effective = rule ?? new RetentionRuleSettings();

        return effective with
        {

            Days = ArcanumSettingClamps.RetentionRuleDays(effective.Days),

        };

    }

}
