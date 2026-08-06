using System.Globalization;

using System.Text.Json.Serialization.Metadata;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Backup;

namespace RetroDownfall.Arcanum.Cli.Commands;

internal sealed class BackupCommands(
    IBackupService backupService,
    IBackupRestoreService restoreService,
    IBackupPassphraseReader passphraseReader,
    IConfirmationPrompt confirmationPrompt,
    IConsoleDispatcher dispatcher,
    ICliInvocationContext invocationContext)
{

    public async Task<int> Create(
        string? scope,
        Guid? sessionId,
        string[] includes,
        string[] excludes,
        string? outputPath,
        bool dryRun,
        bool overwrite,
        string? passphraseEnvironmentVariable,
        int? passphraseFileDescriptor,
        CancellationToken cancellationToken)
    {

        if (!TryBuildPlanRequest(
                scope,
                sessionId,
                includes,
                excludes,
                out BackupPlanRequest planRequest))
        {

            return (int)CliExitCode.ConfigurationError;

        }

        if (dryRun)
        {

            BackupPlan plan = await backupService
                .PlanAsync(planRequest, cancellationToken)
                .ConfigureAwait(false);

            Write(
                plan,
                CliJsonContext.Default.BackupPlan,
                WritePlan);

            return (int)CliExitCode.Success;

        }

        if (!ValidatePassphraseSources(
                passphraseEnvironmentVariable,
                passphraseFileDescriptor))
        {

            return (int)CliExitCode.ConfigurationError;

        }

        using SensitiveBackupPassphrase passphrase = await ReadRequiredPassphraseAsync(
                passphraseEnvironmentVariable,
                passphraseFileDescriptor,
                BackupPassphraseReadPurpose.CreateArchive,
                cancellationToken)
            .ConfigureAwait(false);

        BackupCreateResult result = await backupService
            .CreateAsync(
                new BackupCreateRequest(
                    planRequest,
                    outputPath,
                    overwrite),
                passphrase.Value,
                cancellationToken)
            .ConfigureAwait(false);

        Write(
            result,
            CliJsonContext.Default.BackupCreateResult,
            WriteCreateResult);

        return result.Status == BackupCreateStatus.Complete
            ? (int)CliExitCode.Success
            : (int)CliExitCode.GenericError;

    }

    public async Task<int> Inspect(
        string archivePath,
        bool decrypt,
        string? passphraseEnvironmentVariable,
        int? passphraseFileDescriptor,
        CancellationToken cancellationToken)
    {

        if (!ValidatePassphraseSources(
                passphraseEnvironmentVariable,
                passphraseFileDescriptor))
        {

            return (int)CliExitCode.ConfigurationError;

        }

        BackupPassphraseReadPurpose purpose = decrypt
            ? BackupPassphraseReadPurpose.OpenArchive
            : BackupPassphraseReadPurpose.InspectOuterMetadata;

        using SensitiveBackupPassphrase? passphrase = await passphraseReader
            .ReadAsync(
                new BackupPassphraseReadRequest(
                    passphraseEnvironmentVariable,
                    passphraseFileDescriptor,
                    purpose),
                cancellationToken)
            .ConfigureAwait(false);

        BackupInspectResult result = await backupService
            .InspectAsync(
                archivePath,
                passphrase?.Value,
                cancellationToken)
            .ConfigureAwait(false);

        Write(
            result,
            CliJsonContext.Default.BackupInspectResult,
            WriteInspectResult);

        return (int)CliExitCode.Success;

    }

    public async Task<int> Verify(
        string archivePath,
        string? passphraseEnvironmentVariable,
        int? passphraseFileDescriptor,
        CancellationToken cancellationToken)
    {

        if (!ValidatePassphraseSources(
                passphraseEnvironmentVariable,
                passphraseFileDescriptor))
        {

            return (int)CliExitCode.ConfigurationError;

        }

        using SensitiveBackupPassphrase passphrase = await ReadRequiredPassphraseAsync(
                passphraseEnvironmentVariable,
                passphraseFileDescriptor,
                BackupPassphraseReadPurpose.OpenArchive,
                cancellationToken)
            .ConfigureAwait(false);

        BackupVerifyResult result = await backupService
            .VerifyAsync(
                archivePath,
                passphrase.Value,
                cancellationToken)
            .ConfigureAwait(false);

        Write(
            result,
            CliJsonContext.Default.BackupVerifyResult,
            WriteVerifyResult);

        return result.IsValid
            ? (int)CliExitCode.Success
            : (int)CliExitCode.GenericError;

    }

    public async Task<int> List(
        string? directory,
        CancellationToken cancellationToken)
    {

        IReadOnlyList<BackupListItem> result = await backupService
            .ListAsync(directory, cancellationToken)
            .ConfigureAwait(false);

        BackupListItem[] items = [.. result];

        Write(
            items,
            CliJsonContext.Default.BackupListItemArray,
            WriteList);

        return (int)CliExitCode.Success;

    }

    public async Task<int> Restore(
        string archivePath,
        string? conflictMode,
        string? destinationRoot,
        Guid[] sessionIds,
        string[] mappings,
        bool restoreMasterApiKey,
        bool dryRun,
        bool skipSafetyBackup,
        string? passphraseEnvironmentVariable,
        int? passphraseFileDescriptor,
        CancellationToken cancellationToken)
    {

        if (!ValidatePassphraseSources(
                passphraseEnvironmentVariable,
                passphraseFileDescriptor))
        {

            return (int)CliExitCode.ConfigurationError;

        }

        if (!BackupCliCatalog.TryParseConflictMode(
                conflictMode ?? "replace-installation",
                out BackupRestoreConflictMode mode))
        {

            dispatcher.WriteDiagnostic(
                "--conflict-mode must name a supported mode: "
                + BackupCliCatalog.ConflictModeHelp + ".");

            return (int)CliExitCode.ConfigurationError;

        }

        if (!TryParseMappings(mappings, out BackupPathMapping[] pathMappings))
        {

            return (int)CliExitCode.ConfigurationError;

        }

        using SensitiveBackupPassphrase passphrase = await ReadRequiredPassphraseAsync(
                passphraseEnvironmentVariable,
                passphraseFileDescriptor,
                BackupPassphraseReadPurpose.OpenArchive,
                cancellationToken)
            .ConfigureAwait(false);

        BackupRestoreRequest request = new(
            archivePath,
            mode,
            destinationRoot,
            sessionIds,
            pathMappings,
            restoreMasterApiKey,
            dryRun,
            Confirmed: false,
            CreateSafetyBackup: !skipSafetyBackup);

        if (dryRun)
        {

            BackupRestorePlan plan = await restoreService
                .PlanAsync(request, passphrase.Value, cancellationToken)
                .ConfigureAwait(false);

            Write(plan, CliJsonContext.Default.BackupRestorePlan, WriteRestorePlan);

            return plan.Blockers.Length == 0
                ? (int)CliExitCode.Success
                : (int)CliExitCode.GenericError;

        }

        // Replacing an installation is the only destructive mode. The shared prompt already
        // short-circuits on the global `--yes`, so there is no second opt-out flag to learn.
        if (mode == BackupRestoreConflictMode.ReplaceInstallation)
        {

            bool confirmed = await confirmationPrompt
                .PromptForConfirmationAsync(
                    "Replace the current Arcanum installation with this archive? The displaced "
                    + "installation is retained until cleanup"
                    + (skipSafetyBackup
                        ? " and no pre-restore safety backup will be written."
                        : ", and a pre-restore safety backup is written first."),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!confirmed)
            {

                dispatcher.WriteDiagnostic("Restore cancelled.");

                return (int)CliExitCode.Success;

            }

            request = request with { Confirmed = true };

        }

        BackupRestoreResult result = await restoreService
            .RestoreAsync(request, passphrase.Value, cancellationToken)
            .ConfigureAwait(false);

        Write(result, CliJsonContext.Default.BackupRestoreResult, WriteRestoreResult);

        return result.Status is BackupRestoreStatus.Completed or BackupRestoreStatus.DryRunCompleted
            ? (int)CliExitCode.Success
            : (int)CliExitCode.GenericError;

    }

    public async Task<int> Migrate(
        string archivePath,
        string? outputPath,
        bool overwrite,
        string? passphraseEnvironmentVariable,
        int? passphraseFileDescriptor,
        CancellationToken cancellationToken)
    {

        if (!ValidatePassphraseSources(
                passphraseEnvironmentVariable,
                passphraseFileDescriptor))
        {

            return (int)CliExitCode.ConfigurationError;

        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {

            dispatcher.WriteDiagnostic(
                "--output is required: migration always writes a new archive and never rewrites the source.");

            return (int)CliExitCode.ConfigurationError;

        }

        using SensitiveBackupPassphrase passphrase = await ReadRequiredPassphraseAsync(
                passphraseEnvironmentVariable,
                passphraseFileDescriptor,
                BackupPassphraseReadPurpose.OpenArchive,
                cancellationToken)
            .ConfigureAwait(false);

        BackupMigrateResult result = await restoreService
            .MigrateAsync(
                new BackupMigrateRequest(archivePath, outputPath, overwrite),
                passphrase.Value,
                cancellationToken)
            .ConfigureAwait(false);

        Write(result, CliJsonContext.Default.BackupMigrateResult, WriteMigrateResult);

        return result.Migrated
            ? (int)CliExitCode.Success
            : (int)CliExitCode.GenericError;

    }

    private bool TryParseMappings(
        IReadOnlyList<string> values,
        out BackupPathMapping[] mappings)
    {

        List<BackupPathMapping> parsed = [];

        foreach (string value in values)
        {

            string[] parts = value.Split('=', 3);

            if (parts.Length != 3
                || !BackupCliCatalog.TryParseMappingKind(parts[0], out BackupPathMappingKind kind))
            {

                dispatcher.WriteDiagnostic(
                    "--map must use <kind>=<from>=<to> with a supported kind: "
                    + BackupCliCatalog.MappingKindHelp + ".");

                mappings = [];

                return false;

            }

            parsed.Add(new BackupPathMapping(kind, parts[1], parts[2]));

        }

        mappings = [.. parsed];

        return true;

    }

    private void WriteRestorePlan(BackupRestorePlan plan)
    {

        dispatcher.WritePayload("Restore plan");

        dispatcher.WritePayload($"Generated: {plan.GeneratedAt:O}");

        dispatcher.WritePayload($"Archive: {plan.ArchivePath} (format {plan.FormatVersion})");

        dispatcher.WritePayload(
            $"Mode: {BackupCliCatalog.Format(plan.ConflictMode)}; destination: {plan.DestinationRoot}");

        dispatcher.WritePayload(
            $"Restores: {FormatCount(plan.Entries)} entries, {FormatCount(plan.RestoredBytes)} bytes");

        dispatcher.WritePayload(
            $"Capacity: {FormatCount(plan.RequiredBytes)} bytes required, "
            + $"{FormatCount(plan.AvailableBytes)} bytes available");

        dispatcher.WritePayload(
            $"Schema: {plan.SourceSchemaIdentity} -> {plan.DestinationSchemaIdentity} "
            + $"(migration {(plan.SchemaMigrationRequired ? "required" : "not required")})");

        dispatcher.WritePayload(
            $"Confirmation required: {(plan.RequiresConfirmation ? "yes" : "no")}; "
            + $"safety backup planned: {(plan.SafetyBackupPlanned ? "yes" : "no")}");

        foreach (BackupComponent component in plan.Components)
        {

            dispatcher.WritePayload($"  Component: {BackupCliCatalog.Format(component)}");

        }

        foreach (Guid sessionId in plan.SelectedSessionIds)
        {

            dispatcher.WritePayload($"  Session: {sessionId:D}");

        }

        foreach (BackupRestoreMappingPlan mapping in plan.PathMappings)
        {

            dispatcher.WritePayload(
                $"  Map {BackupCliCatalog.Format(mapping.Kind)}: {mapping.From} -> {mapping.To} "
                + $"({FormatCount(mapping.MatchedTargets)} matched)");

        }

        foreach (string path in plan.UnmappedNonportablePaths)
        {

            dispatcher.WritePayload($"  Unmapped: {path}");

        }

        foreach (string warning in plan.Warnings)
        {

            dispatcher.WritePayload($"Warning: {warning}");

        }

        WriteIssues(plan.Blockers);

    }

    private void WriteRestoreResult(BackupRestoreResult result)
    {

        dispatcher.WritePayload($"Restore: {BackupCliCatalog.Format(result.Status)}");

        dispatcher.WritePayload($"Operation: {result.OperationId:D}");

        dispatcher.WritePayload($"Archive: {result.ArchivePath}");

        dispatcher.WritePayload($"Destination: {result.DestinationRoot}");

        if (result.SafetyBackupPath is not null)
        {

            dispatcher.WritePayload($"Pre-restore safety backup: {result.SafetyBackupPath}");

        }

        foreach (BackupRestorePhaseRecord phase in result.Phases)
        {

            dispatcher.WritePayload($"  {phase.Phase}: {phase.Detail}");

        }

        if (result.Reconciliation is BackupRestoreReconciliation reconciliation)
        {

            dispatcher.WritePayload(
                $"Reconciled: {FormatCount(reconciliation.Attachments)} attachments "
                + $"({FormatCount(reconciliation.StaleAttachmentSources)} stale sources), "
                + $"{FormatCount(reconciliation.UploadedFiles)} uploaded files, "
                + $"{FormatCount(reconciliation.BatchFiles)} batches, "
                + $"{FormatCount(reconciliation.EmbeddingsRebuilt)} embeddings to rebuild, "
                + $"{FormatCount(reconciliation.PendingOperationsCleared)} pending operations cleared");

            foreach (string issue in reconciliation.Issues)
            {

                dispatcher.WritePayload($"Reconciliation: {issue}");

            }

        }

        foreach (string warning in result.Plan.Warnings)
        {

            dispatcher.WritePayload($"Warning: {warning}");

        }

        WriteIssues(result.Issues);

    }

    private void WriteMigrateResult(BackupMigrateResult result)
    {

        dispatcher.WritePayload(
            result.Migrated
                ? "Archive migration: complete"
                : "Archive migration: refused");

        dispatcher.WritePayload($"Source: {result.ArchivePath} (format {result.SourceFormatVersion})");

        if (result.OutputPath is not null)
        {

            dispatcher.WritePayload(
                $"Output: {result.OutputPath} (format {result.OutputFormatVersion}, "
                + $"{FormatCount(result.OutputBytes)} bytes)");

        }

        WriteIssues(result.Issues);

    }

    private async ValueTask<SensitiveBackupPassphrase> ReadRequiredPassphraseAsync(
        string? environmentVariableName,
        int? fileDescriptor,
        BackupPassphraseReadPurpose purpose,
        CancellationToken cancellationToken)
    {

        SensitiveBackupPassphrase? passphrase = await passphraseReader
            .ReadAsync(
                new BackupPassphraseReadRequest(
                    environmentVariableName,
                    fileDescriptor,
                    purpose),
                cancellationToken)
            .ConfigureAwait(false);

        return passphrase
            ?? throw new BackupPassphraseInputException(
                "A backup passphrase is required.");

    }

    private bool TryBuildPlanRequest(
        string? scopeValue,
        Guid? sessionId,
        IReadOnlyList<string> includeValues,
        IReadOnlyList<string> excludeValues,
        out BackupPlanRequest request)
    {

        request = null!;

        if (!BackupCliCatalog.TryParseScope(scopeValue ?? "full", out BackupScope scope))
        {

            dispatcher.WriteDiagnostic(
                "--scope must name a supported backup scope: "
                + BackupCliCatalog.ScopeHelp + ".");

            return false;

        }

        if (!TryParseComponents("--include", includeValues, out BackupComponent[] includes)
            || !TryParseComponents("--exclude", excludeValues, out BackupComponent[] excludes))
        {

            return false;

        }

        if (scope == BackupScope.SpecificSession && !sessionId.HasValue)
        {

            dispatcher.WriteDiagnostic(
                "--session-id is required when --scope is specific-session.");

            return false;

        }

        request = new BackupPlanRequest(
            scope,
            sessionId,
            includes,
            excludes);

        return true;

    }

    private bool TryParseComponents(
        string option,
        IReadOnlyList<string> values,
        out BackupComponent[] components)
    {

        List<BackupComponent> parsed = [];

        HashSet<BackupComponent> seen = [];

        foreach (string value in values)
        {

            if (!BackupCliCatalog.TryParseComponent(value, out BackupComponent component))
            {

                dispatcher.WriteDiagnostic(
                    option + " must use supported backup components: "
                    + BackupCliCatalog.ComponentHelp + ".");

                components = [];

                return false;

            }

            if (seen.Add(component))
            {

                parsed.Add(component);

            }

        }

        components = [.. parsed];

        return true;

    }

    private bool ValidatePassphraseSources(
        string? environmentVariableName,
        int? fileDescriptor)
    {

        if (environmentVariableName is not null && fileDescriptor.HasValue)
        {

            dispatcher.WriteDiagnostic(
                "Specify exactly one passphrase source: --passphrase-env or --passphrase-fd.");

            return false;

        }

        if (fileDescriptor < 0)
        {

            dispatcher.WriteDiagnostic(
                "--passphrase-fd must be zero or greater.");

            return false;

        }

        return true;

    }

    private void Write<T>(
        T value,
        JsonTypeInfo<T> jsonType,
        Action<T> writeText)
    {

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(value, jsonType);

        }
        else
        {

            writeText(value);

        }

    }

    private void WritePlan(BackupPlan plan)
    {

        dispatcher.WritePayload("Backup plan");

        dispatcher.WritePayload($"Generated: {plan.GeneratedAt:O}");

        dispatcher.WritePayload($"Scope: {BackupCliCatalog.Format(plan.Scope)}");

        if (plan.SessionId.HasValue)
        {

            dispatcher.WritePayload($"Session: {plan.SessionId.Value:D}");

        }

        dispatcher.WritePayload(
            $"Estimated: {FormatCount(plan.EstimatedFiles)} files, "
            + $"{FormatCount(plan.EstimatedBytes)} bytes");

        foreach (BackupPlanComponent component in plan.Components)
        {

            dispatcher.WritePayload(
                $"  {BackupCliCatalog.Format(component.Component)}: "
                + $"{BackupCliCatalog.Format(component.Status)}, "
                + $"{FormatCount(component.Files)} files, "
                + $"{FormatCount(component.EstimatedBytes)} bytes - "
                + component.Detail);

            foreach (string path in component.NonportablePaths)
            {

                dispatcher.WritePayload($"    Nonportable: {path}");

            }

        }

        foreach (string path in plan.MissingFiles)
        {

            dispatcher.WritePayload($"Missing: {path}");

        }

        foreach (string warning in plan.SecurityWarnings)
        {

            dispatcher.WritePayload($"Security warning: {warning}");

        }

    }

    private void WriteCreateResult(BackupCreateResult result)
    {

        dispatcher.WritePayload(
            result.Status == BackupCreateStatus.Complete
                ? "Backup complete"
                : "Backup incomplete");

        dispatcher.WritePayload($"Operation: {result.OperationId:D}");

        if (result.ArchivePath is not null)
        {

            dispatcher.WritePayload($"Archive: {result.ArchivePath}");

        }

        dispatcher.WritePayload($"Archive bytes: {FormatCount(result.ArchiveBytes)}");

        WriteIssues(result.Issues);

    }

    private void WriteInspectResult(BackupInspectResult result)
    {

        dispatcher.WritePayload("Backup archive");

        dispatcher.WritePayload($"Path: {result.ArchivePath}");

        dispatcher.WritePayload($"Archive bytes: {FormatCount(result.ArchiveBytes)}");

        dispatcher.WritePayload($"Format version: {result.FormatVersion}");

        dispatcher.WritePayload($"Created: {result.Header.CreatedAt:O}");

        dispatcher.WritePayload(
            $"Envelope: {result.Header.Kdf}, "
            + $"{FormatCount(result.Header.KdfIterations)} iterations, "
            + $"{result.Header.Encryption}, "
            + $"{FormatCount(result.Header.ChunkSize)} byte chunks");

        if (result.Manifest is null)
        {

            dispatcher.WritePayload("Manifest: encrypted (passphrase not supplied)");

            return;

        }

        dispatcher.WritePayload("Manifest: decrypted");

        dispatcher.WritePayload(
            $"Arcanum: {result.Manifest.ArcanumVersion} ({result.Manifest.Build})");

        dispatcher.WritePayload(
            $"Scope: {BackupCliCatalog.Format(result.Manifest.Scope)}; "
            + $"entries: {FormatCount(result.Manifest.Entries.LongLength)}");

    }

    private void WriteVerifyResult(BackupVerifyResult result)
    {

        dispatcher.WritePayload(
            result.IsValid
                ? "Backup verification: valid"
                : "Backup verification: INVALID");

        dispatcher.WritePayload($"Path: {result.ArchivePath}");

        dispatcher.WritePayload($"Format version: {result.FormatVersion}");

        dispatcher.WritePayload(
            $"Verified: {FormatCount(result.VerifiedEntries)} entries, "
            + $"{FormatCount(result.VerifiedBytes)} bytes");

        dispatcher.WritePayload(
            $"Database readable: {(result.DatabaseReadable ? "yes" : "no")}");

        WriteIssues(result.Issues);

    }

    private void WriteList(BackupListItem[] items)
    {

        if (items.Length == 0)
        {

            dispatcher.WritePayload("No backup archives found.");

            return;

        }

        foreach (BackupListItem item in items)
        {

            dispatcher.WritePayload(
                $"{item.ArchivePath} - {FormatCount(item.ArchiveBytes)} bytes, "
                + $"format {item.Header.FormatVersion}, "
                + $"created {item.Header.CreatedAt:O}");

        }

    }

    private void WriteIssues(IEnumerable<BackupVerifyIssue> issues)
    {

        foreach (BackupVerifyIssue issue in issues)
        {

            string path = issue.Path is null
                ? string.Empty
                : $" ({issue.Path})";

            dispatcher.WritePayload(
                $"Issue: {issue.Code}{path} - {issue.Message}");

        }

    }

    private static string FormatCount(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

}

internal static class BackupCliCatalog
{

    private static readonly IReadOnlyDictionary<string, BackupScope> Scopes =
        new Dictionary<string, BackupScope>(StringComparer.OrdinalIgnoreCase)
        {

            ["full"] = BackupScope.Full,

            ["configuration-and-authored-assets"] = BackupScope.ConfigurationAndAuthoredAssets,

            ["sessions-and-memory"] = BackupScope.SessionsAndMemory,

            ["specific-session"] = BackupScope.SpecificSession,

            ["metadata-only"] = BackupScope.MetadataOnly,

        };

    private static readonly IReadOnlyDictionary<string, BackupComponent> Components =
        new Dictionary<string, BackupComponent>(StringComparer.OrdinalIgnoreCase)
        {

            ["grimoire-database"] = BackupComponent.GrimoireDatabase,

            ["grimoire-kdf-metadata"] = BackupComponent.GrimoireKdfMetadata,

            ["portable-recovery-keys"] = BackupComponent.PortableRecoveryKeys,

            ["configuration"] = BackupComponent.Configuration,

            ["session-attachments"] = BackupComponent.SessionAttachments,

            ["uploaded-files"] = BackupComponent.UploadedFiles,

            ["batch-artifacts"] = BackupComponent.BatchArtifacts,

            ["global-codex"] = BackupComponent.GlobalCodex,

            ["global-spells"] = BackupComponent.GlobalSpells,

            ["mcp-configuration"] = BackupComponent.McpConfiguration,

            ["trusted-mcp-workspace-metadata"] = BackupComponent.TrustedMcpWorkspaceMetadata,

            ["cli-state"] = BackupComponent.CliState,

            ["the-forge-state"] = BackupComponent.TheForgeState,

            ["compendium-settings"] = BackupComponent.CompendiumSettings,

            ["compendium-certificates"] = BackupComponent.CompendiumCertificates,

            ["audit-logs"] = BackupComponent.AuditLogs,

            ["guardrail-logs"] = BackupComponent.GuardrailLogs,

            ["master-api-key"] = BackupComponent.MasterApiKey,

        };

    private static readonly IReadOnlyDictionary<string, BackupRestoreConflictMode> ConflictModes =
        new Dictionary<string, BackupRestoreConflictMode>(StringComparer.OrdinalIgnoreCase)
        {

            ["replace-installation"] = BackupRestoreConflictMode.ReplaceInstallation,

            ["new-profile-root"] = BackupRestoreConflictMode.NewProfileRoot,

            ["import-selected-sessions"] = BackupRestoreConflictMode.ImportSelectedSessions,

        };

    private static readonly IReadOnlyDictionary<string, BackupPathMappingKind> MappingKinds =
        new Dictionary<string, BackupPathMappingKind>(StringComparer.OrdinalIgnoreCase)
        {

            ["campaign-root"] = BackupPathMappingKind.CampaignRoot,

            ["workspace-root"] = BackupPathMappingKind.WorkspaceRoot,

            ["codex-root"] = BackupPathMappingKind.CodexRoot,

            ["spell-root"] = BackupPathMappingKind.SpellRoot,

            ["attachment-source"] = BackupPathMappingKind.AttachmentSourceProvenance,

        };

    public static string ScopeHelp => string.Join(", ", Scopes.Keys);

    public static string ComponentHelp => string.Join(", ", Components.Keys);

    public static string ConflictModeHelp => string.Join(", ", ConflictModes.Keys);

    public static string MappingKindHelp => string.Join(", ", MappingKinds.Keys);

    public static bool TryParseConflictMode(string value, out BackupRestoreConflictMode mode) =>
        ConflictModes.TryGetValue(value, out mode);

    public static bool TryParseMappingKind(string value, out BackupPathMappingKind kind) =>
        MappingKinds.TryGetValue(value, out kind);

    public static string Format(BackupRestoreConflictMode mode) =>
        ConflictModes.FirstOrDefault(pair => pair.Value == mode).Key ?? mode.ToString();

    public static string Format(BackupPathMappingKind kind) =>
        MappingKinds.FirstOrDefault(pair => pair.Value == kind).Key ?? kind.ToString();

    public static string Format(BackupRestoreStatus status) =>
        status switch
        {
            BackupRestoreStatus.Completed => "completed",
            BackupRestoreStatus.DryRunCompleted => "dry-run completed",
            BackupRestoreStatus.Rejected => "REJECTED",
            BackupRestoreStatus.RolledBack => "ROLLED BACK",
            BackupRestoreStatus.ReconciliationRequired => "RECONCILIATION REQUIRED",
            _ => status.ToString(),
        };

    public static bool TryParseScope(string value, out BackupScope scope) =>
        Scopes.TryGetValue(value, out scope);

    public static bool TryParseComponent(
        string value,
        out BackupComponent component) =>
        Components.TryGetValue(value, out component);

    public static string Format(BackupScope scope) =>
        scope switch
        {
            BackupScope.Full => "full",
            BackupScope.ConfigurationAndAuthoredAssets => "configuration-and-authored-assets",
            BackupScope.SessionsAndMemory => "sessions-and-memory",
            BackupScope.SpecificSession => "specific-session",
            BackupScope.MetadataOnly => "metadata-only",
            _ => scope.ToString(),
        };

    public static string Format(BackupComponent component) =>
        Components.FirstOrDefault(pair => pair.Value == component).Key
        ?? component.ToString();

    public static string Format(BackupComponentStatus status) =>
        status switch
        {
            BackupComponentStatus.Complete => "complete",
            BackupComponentStatus.OmittedByPolicy => "omitted-by-policy",
            BackupComponentStatus.Unavailable => "unavailable",
            BackupComponentStatus.Failed => "failed",
            _ => status.ToString(),
        };

}
