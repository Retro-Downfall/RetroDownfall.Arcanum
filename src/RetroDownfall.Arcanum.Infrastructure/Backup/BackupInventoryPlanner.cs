using System.Buffers;

using System.Security.Cryptography;

using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Configuration;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

public sealed record BackupStatePaths(
    string GrimoireDirectory,
    string SecretStoreDirectory,
    string AuditLogBasePath,
    string GuardrailLogBasePath)
{

    public static BackupStatePaths Default => new(
        ArcanumPaths.GrimoireDirectory,
        ArcanumPaths.SecretStoreDirectory,
        Path.Combine(ArcanumPaths.GrimoireDirectory, "audit.jsonl"),
        Path.Combine(ArcanumPaths.GrimoireDirectory, "guardrails.jsonl"));

    public string DatabasePath => Path.Combine(GrimoireDirectory, "arcanum.db");

    public string KdfPath => DatabasePath + ".kdf";

    public string AttachmentsDirectory => Path.Combine(GrimoireDirectory, "attachments");

    public string FilesDirectory => Path.Combine(GrimoireDirectory, "files");

    public string BackupsDirectory => Path.Combine(GrimoireDirectory, "backups");

}

public sealed record BackupInventoryFile(
    BackupComponent Component,
    string SourcePath,
    string ArchivePath,
    long Size,
    string Sha256,
    ulong VolumeId,
    ulong FileId);

public sealed record BackupInventory(
    BackupPlan Plan,
    IReadOnlyList<BackupInventoryFile> Files,
    IReadOnlySet<string> RequiredFileEncryptionKeyIds);

public sealed class BackupInventoryPlanner(BackupStatePaths paths)
{

    private static readonly BackupComponent[] ExplicitOnlyComponents =
    [
        BackupComponent.TrustedMcpWorkspaceMetadata,
        BackupComponent.AuditLogs,
        BackupComponent.GuardrailLogs,
        BackupComponent.MasterApiKey,
    ];

    public async Task<BackupInventory> BuildAsync(
        BackupPlanRequest request,
        string databasePath,
        string databasePassphrase,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        ValidateRequest(request);

        DateTimeOffset generatedAt = DateTimeOffset.UtcNow;

        HashSet<BackupComponent> selected = DefaultComponents(request.Scope);

        selected.UnionWith(request.Include);

        selected.ExceptWith(request.Exclude);

        List<BackupInventoryFile> files = [];

        Dictionary<BackupComponent, ComponentAccumulator> components = Enum
            .GetValues<BackupComponent>()
            .ToDictionary(
                static component => component,
                component => selected.Contains(component)
                    ? new ComponentAccumulator(component, BackupComponentStatus.Complete, "Selected.")
                    : new ComponentAccumulator(
                        component,
                        BackupComponentStatus.OmittedByPolicy,
                        ExplicitOnlyComponents.Contains(component)
                            ? "Omitted by default; include this typed component explicitly."
                            : "Not selected by this scope or explicitly excluded."));

        List<string> missing = [];

        List<string> warnings = BuildWarnings(request, selected);

        HashSet<string> requiredKeyIds = new(StringComparer.Ordinal);

        if (selected.Contains(BackupComponent.GrimoireDatabase))
        {

            AddRequiredFile(
                BackupComponent.GrimoireDatabase,
                databasePath,
                BackupArchivePaths.GrimoireDatabase,
                files,
                components,
                missing,
                cancellationToken);

        }

        if (selected.Contains(BackupComponent.GrimoireKdfMetadata))
        {

            string kdfPath = databasePath + ".kdf";

            if (!File.Exists(kdfPath)
                && string.Equals(
                    Path.GetFullPath(databasePath),
                    Path.GetFullPath(paths.DatabasePath),
                    StringComparison.Ordinal))
            {

                kdfPath = paths.KdfPath;

            }

            AddRequiredFile(
                BackupComponent.GrimoireKdfMetadata,
                kdfPath,
                BackupArchivePaths.GrimoireKdf,
                files,
                components,
                missing,
                cancellationToken);

        }

        bool databaseAvailable = File.Exists(databasePath);

        if (databaseAvailable
            && selected.Overlaps(
                [
                    BackupComponent.SessionAttachments,
                    BackupComponent.UploadedFiles,
                    BackupComponent.BatchArtifacts,
                ]))
        {

            await AddDatabaseBackedFilesAsync(
                request,
                selected,
                databasePath,
                databasePassphrase,
                files,
                components,
                missing,
                requiredKeyIds,
                cancellationToken).ConfigureAwait(false);

        }

        BackupComponent? configurationOwner = selected.Contains(BackupComponent.Configuration)
            ? BackupComponent.Configuration
            : selected.Contains(BackupComponent.CompendiumSettings)
                ? BackupComponent.CompendiumSettings
                : null;

        if (configurationOwner is BackupComponent owner)
        {

            _ = await ArcanumConfigurationTransaction.RunAsync(
                    () =>
                    {

                        AddConfigurationFiles(
                            owner,
                            files,
                            components[owner],
                            cancellationToken);

                        return Task.FromResult(true);

                    },
                    cancellationToken)
                .ConfigureAwait(false);

        }

        AddOptionalFile(
            BackupComponent.GlobalCodex,
            Path.Combine(paths.GrimoireDirectory, "CODEX.md"),
            "authored/CODEX.md",
            selected,
            files,
            components,
            cancellationToken);

        AddOptionalFile(
            BackupComponent.McpConfiguration,
            Path.Combine(paths.GrimoireDirectory, "mcp.json"),
            "authored/mcp.json",
            selected,
            files,
            components,
            cancellationToken);

        AddTrustedMcpWorkspaceFiles(
            BackupComponent.TrustedMcpWorkspaceMetadata,
            Path.Combine(paths.GrimoireDirectory, "trusted-mcp-workspaces.json"),
            selected,
            files,
            components,
            cancellationToken);

        AddOptionalFiles(
            BackupComponent.CliState,
            [
                "cli-context.json",
                "cli-session.txt",
                "recent-resources.txt",
            ],
            "cli",
            selected,
            files,
            components,
            cancellationToken);

        AddOptionalFiles(
            BackupComponent.TheForgeState,
            [
                "the-forge.json",
                "the-forge-trial-suites.json",
                "the-forge-comparisons.json",
                "the-forge-inference-traces.json",
                "the-forge-diagnostic-mcp-fixtures.json",
            ],
            "forge",
            selected,
            files,
            components,
            cancellationToken);

        AddDirectoryTree(
            BackupComponent.GlobalSpells,
            Path.Combine(paths.GrimoireDirectory, "spells"),
            "authored/spells",
            selected,
            files,
            components,
            missing,
            cancellationToken);

        AddDirectoryTree(
            BackupComponent.CompendiumCertificates,
            Path.Combine(paths.GrimoireDirectory, "certs"),
            "compendium/certificates",
            selected,
            files,
            components,
            missing,
            cancellationToken);

        AddDatedLogs(
            BackupComponent.AuditLogs,
            paths.AuditLogBasePath,
            selected,
            files,
            components,
            cancellationToken);

        AddDatedLogs(
            BackupComponent.GuardrailLogs,
            paths.GuardrailLogBasePath,
            selected,
            files,
            components,
            cancellationToken);

        if (selected.Contains(BackupComponent.CompendiumSettings)
            && selected.Contains(BackupComponent.Configuration))
        {

            ComponentAccumulator config = components[BackupComponent.Configuration];

            components[BackupComponent.CompendiumSettings].Set(
                config.Status,
                "Compendium uses the shared arcanum.json entry; no duplicate file is stored.");

        }

        files.Sort(
            static (left, right) => StringComparer.Ordinal.Compare(
                left.ArchivePath,
                right.ArchivePath));

        EnsureUniqueArchivePaths(files);

        BackupPlanComponent[] planComponents = components.Values
            .OrderBy(static component => component.Component)
            .Select(static component => component.ToPlanComponent())
            .ToArray();

        BackupPlan plan = new(
            generatedAt,
            request.Scope,
            request.SessionId,
            planComponents,
            files.Count,
            files.Sum(static file => file.Size),
            [.. missing.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. warnings.Distinct(StringComparer.Ordinal)]);

        return new BackupInventory(plan, files, requiredKeyIds);

    }

    private static HashSet<BackupComponent> DefaultComponents(BackupScope scope) =>
        scope switch
        {
            BackupScope.Full =>
            [
                BackupComponent.GrimoireDatabase,
                BackupComponent.GrimoireKdfMetadata,
                BackupComponent.PortableRecoveryKeys,
                BackupComponent.Configuration,
                BackupComponent.SessionAttachments,
                BackupComponent.UploadedFiles,
                BackupComponent.BatchArtifacts,
                BackupComponent.GlobalCodex,
                BackupComponent.GlobalSpells,
                BackupComponent.McpConfiguration,
                BackupComponent.CliState,
                BackupComponent.TheForgeState,
                BackupComponent.CompendiumSettings,
                BackupComponent.CompendiumCertificates,
            ],
            BackupScope.ConfigurationAndAuthoredAssets =>
            [
                BackupComponent.Configuration,
                BackupComponent.GlobalCodex,
                BackupComponent.GlobalSpells,
                BackupComponent.McpConfiguration,
                BackupComponent.CliState,
                BackupComponent.TheForgeState,
                BackupComponent.CompendiumSettings,
                BackupComponent.CompendiumCertificates,
            ],
            BackupScope.SessionsAndMemory =>
            [
                BackupComponent.GrimoireDatabase,
                BackupComponent.GrimoireKdfMetadata,
                BackupComponent.PortableRecoveryKeys,
                BackupComponent.SessionAttachments,
                BackupComponent.UploadedFiles,
                BackupComponent.BatchArtifacts,
            ],
            BackupScope.SpecificSession =>
            [
                BackupComponent.GrimoireDatabase,
                BackupComponent.GrimoireKdfMetadata,
                BackupComponent.PortableRecoveryKeys,
                BackupComponent.SessionAttachments,
            ],
            BackupScope.MetadataOnly => [],
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

    private static void ValidateRequest(BackupPlanRequest request)
    {

        if (!Enum.IsDefined(request.Scope))
        {

            throw new ArgumentOutOfRangeException(nameof(request));

        }

        if (request.Scope == BackupScope.SpecificSession && request.SessionId is null)
        {

            throw new ArgumentException("A specific-session backup requires a session id.", nameof(request));

        }

        if (request.Include.Any(static component => !Enum.IsDefined(component))
            || request.Exclude.Any(static component => !Enum.IsDefined(component)))
        {

            throw new ArgumentException("Backup include/exclude values must use the typed component catalog.");

        }

    }

    private static List<string> BuildWarnings(
        BackupPlanRequest request,
        IReadOnlySet<BackupComponent> selected)
    {

        List<string> warnings = [];

        if (selected.Contains(BackupComponent.McpConfiguration))
        {

            warnings.Add("Global MCP configuration can contain literal environment values and is sensitive.");

        }

        if (selected.Contains(BackupComponent.TrustedMcpWorkspaceMetadata))
        {

            warnings.Add("Trusted MCP workspace metadata can contain nonportable absolute workspace paths.");

        }

        if (selected.Contains(BackupComponent.AuditLogs))
        {

            warnings.Add("Inference audit logs were explicitly selected and can contain sensitive operational metadata.");

        }

        if (selected.Contains(BackupComponent.GuardrailLogs))
        {

            warnings.Add("Guardrail audit logs were explicitly selected and can contain sensitive violation metadata.");

        }

        if (selected.Contains(BackupComponent.MasterApiKey))
        {

            warnings.Add("The master API key was explicitly selected as sensitive recovery material.");

        }

        if (request.Scope is BackupScope.SessionsAndMemory or BackupScope.SpecificSession)
        {

            warnings.Add(
                "The version-1 physical Grimoire snapshot is indivisible and includes collateral global/accounting rows; the manifest discloses this explicitly.");

        }

        warnings.Add(
            "Environment secrets, OS credential-store internals, raw Data Protection keys, external workspace trees, daemon registration, and ephemeral process state are not portable backup components.");

        return warnings;

    }

    private async Task AddDatabaseBackedFilesAsync(
        BackupPlanRequest request,
        IReadOnlySet<BackupComponent> selected,
        string databasePath,
        string databasePassphrase,
        List<BackupInventoryFile> files,
        IReadOnlyDictionary<BackupComponent, ComponentAccumulator> components,
        List<string> missing,
        HashSet<string> requiredKeyIds,
        CancellationToken cancellationToken)
    {

        string connectionString = BuildConnectionString(databasePath, databasePassphrase);

        await using SqliteConnection connection = new(connectionString);

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (selected.Contains(BackupComponent.SessionAttachments))
        {

            await AddAttachmentsAsync(
                connection,
                request,
                files,
                components[BackupComponent.SessionAttachments],
                missing,
                requiredKeyIds,
                cancellationToken).ConfigureAwait(false);

        }

        if (selected.Contains(BackupComponent.UploadedFiles)
            || selected.Contains(BackupComponent.BatchArtifacts))
        {

            await AddUploadedAndBatchFilesAsync(
                connection,
                selected,
                files,
                components,
                missing,
                requiredKeyIds,
                cancellationToken).ConfigureAwait(false);

        }

    }

    private async Task AddAttachmentsAsync(
        SqliteConnection connection,
        BackupPlanRequest request,
        List<BackupInventoryFile> files,
        ComponentAccumulator component,
        List<string> missing,
        HashSet<string> requiredKeyIds,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = request.Scope == BackupScope.SpecificSession
            ? """
                SELECT "RelativePath", "EncryptionVersion", "EncryptionKeyId"
                FROM "SessionAttachments"
                WHERE "SessionId" = $sessionId;
                """
            : """
                SELECT "RelativePath", "EncryptionVersion", "EncryptionKeyId"
                FROM "SessionAttachments";
                """;

        if (request.Scope == BackupScope.SpecificSession)
        {

            // Canonical, because that is what "SessionAttachments"."SessionId" holds. A bare ToString()
            // matched nothing under BINARY collation, and the failure was silent in the worst possible
            // way: the reader below simply never iterated, so a session-scoped archive reported no
            // missing path and no failure while omitting every attachment blob the database still
            // names. Nothing downstream re-checks that, so the archive was permanently incomplete.
            command.Parameters.AddWithValue(
                "$sessionId",
                request.SessionId!.Value.ToString().ToUpperInvariant());

        }

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            string relative = reader.GetString(0).Replace('\\', '/');

            int encryptionVersion = reader.GetInt32(1);

            string? encryptionKeyId = reader.IsDBNull(2)
                ? null
                : reader.GetString(2);

            if (!TryResolveContainedPath(
                    paths.AttachmentsDirectory,
                    relative,
                    out string source))
            {

                component.Set(
                    BackupComponentStatus.Failed,
                    "A database-backed path is non-canonical or crosses a symbolic link or reparse point.");

                component.NonportablePaths.Add(relative);

                continue;

            }

            if (!await TryAddRequiredBlobCandidateAsync(
                    BackupComponent.SessionAttachments,
                    source,
                    "attachments/" + relative,
                    EncryptedBlobPurpose.SessionAttachment,
                    encryptionVersion,
                    encryptionKeyId,
                    files,
                    component,
                    missing,
                    requiredKeyIds,
                    cancellationToken).ConfigureAwait(false))
            {

                continue;

            }

        }

    }

    private async Task AddUploadedAndBatchFilesAsync(
        SqliteConnection connection,
        IReadOnlySet<BackupComponent> selected,
        List<BackupInventoryFile> files,
        IReadOnlyDictionary<BackupComponent, ComponentAccumulator> components,
        List<string> missing,
        HashSet<string> requiredKeyIds,
        CancellationToken cancellationToken)
    {

        HashSet<Guid> batchIds = [];

        await using (SqliteCommand batchCommand = connection.CreateCommand())
        {

            batchCommand.CommandText = """
                SELECT "InputFileId", "OutputFileId", "ErrorFileId"
                FROM "Batches";
                """;

            await using SqliteDataReader batchReader = await batchCommand
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await batchReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                for (int ordinal = 0; ordinal < 3; ordinal++)
                {

                    if (!batchReader.IsDBNull(ordinal))
                    {

                        string id = batchReader.GetString(ordinal);

                        if (!Guid.TryParse(id, out Guid batchFileId))
                        {

                            if (selected.Contains(BackupComponent.BatchArtifacts))
                            {

                                components[BackupComponent.BatchArtifacts].Set(
                                    BackupComponentStatus.Failed,
                                    "Batch metadata contains a non-canonical uploaded-file id.");

                                components[BackupComponent.BatchArtifacts]
                                    .NonportablePaths
                                    .Add(id);

                            }

                            continue;

                        }

                        _ = batchIds.Add(batchFileId);

                    }

                }

            }

        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT "Id", "EncryptionVersion", "EncryptionKeyId", "Purpose"
            FROM "UploadedFiles";
            """;

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        HashSet<Guid> uploadedIds = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            string id = reader.GetString(0);

            if (!Guid.TryParse(id, out Guid fileId))
            {

                throw new InvalidDataException("UploadedFiles contains an invalid file id.");

            }

            _ = uploadedIds.Add(fileId);

            BackupComponent owner = batchIds.Contains(fileId)
                ? BackupComponent.BatchArtifacts
                : BackupComponent.UploadedFiles;

            if (!selected.Contains(owner))
            {

                continue;

            }

            int encryptionVersion = reader.GetInt32(1);

            string? encryptionKeyId = reader.IsDBNull(2)
                ? null
                : reader.GetString(2);

            string relative = fileId.ToString("N");

            if (!TryResolveContainedPath(
                    paths.FilesDirectory,
                    relative,
                    out string source))
            {

                components[owner].Set(
                    BackupComponentStatus.Failed,
                    "A database-backed path is non-canonical or crosses a symbolic link or reparse point.");

                components[owner].NonportablePaths.Add(relative);

                continue;

            }

            EncryptedBlobPurpose purpose = UploadedFileStorage.ResolveEncryptionPurpose(
                reader.GetString(3));

            if (!await TryAddRequiredBlobCandidateAsync(
                    owner,
                    source,
                    "files/" + relative,
                    purpose,
                    encryptionVersion,
                    encryptionKeyId,
                    files,
                    components[owner],
                    missing,
                    requiredKeyIds,
                    cancellationToken).ConfigureAwait(false))
            {

                continue;

            }

        }

        if (selected.Contains(BackupComponent.BatchArtifacts))
        {

            foreach (Guid missingBatchId in batchIds.Except(uploadedIds))
            {

                components[BackupComponent.BatchArtifacts].Set(
                    BackupComponentStatus.Failed,
                    "A batch references uploaded-file metadata that is missing.");

                missing.Add(
                    Path.Combine(
                        paths.FilesDirectory,
                        missingBatchId.ToString("N")));

            }

        }

    }

    private static void FailEncryptedMetadataWithoutEnvelope(
        int encryptionVersion,
        string? encryptionKeyId,
        ComponentAccumulator component)
    {

        if (encryptionVersion <= 0)
        {

            return;

        }

        component.Set(
            BackupComponentStatus.Failed,
            string.IsNullOrWhiteSpace(encryptionKeyId)
                ? "Encrypted blob metadata is missing its key id, and the captured file has no ARCABLOB envelope."
                : "Encrypted blob metadata and key id identify encrypted content, but the captured file has no ARCABLOB envelope.");

    }

    private void AddOptionalFiles(
        BackupComponent component,
        IEnumerable<string> names,
        string archiveRoot,
        IReadOnlySet<BackupComponent> selected,
        List<BackupInventoryFile> files,
        IReadOnlyDictionary<BackupComponent, ComponentAccumulator> components,
        CancellationToken cancellationToken)
    {

        if (!selected.Contains(component))
        {

            return;

        }

        bool any = false;

        foreach (string name in names)
        {

            string source = Path.Combine(paths.GrimoireDirectory, name);

            if (!File.Exists(source))
            {

                continue;

            }

            AddCandidate(
                component,
                source,
                archiveRoot + "/" + name,
                files,
                components[component],
                cancellationToken);

            any = true;

        }

        if (!any)
        {

            components[component].Set(
                BackupComponentStatus.Unavailable,
                "No state exists for this optional component.");

        }

    }

    private void AddConfigurationFiles(
        BackupComponent component,
        List<BackupInventoryFile> files,
        ComponentAccumulator accumulator,
        CancellationToken cancellationToken)
    {

        string configuration = Path.Combine(
            paths.GrimoireDirectory,
            "arcanum.json");

        string state = Path.Combine(
            paths.GrimoireDirectory,
            "arcanum.preset.json");

        string rollback = Path.Combine(
            paths.GrimoireDirectory,
            "arcanum.preset.rollback.json");

        string journal = Path.Combine(
            paths.GrimoireDirectory,
            "arcanum.preset.journal.json");

        if (File.Exists(journal))
        {

            accumulator.Set(
                BackupComponentStatus.Failed,
                "An interrupted preset transaction must be recovered before configuration backup.");

            return;

        }

        if (!File.Exists(configuration))
        {

            accumulator.Set(
                BackupComponentStatus.Unavailable,
                "No state exists for this optional component.");

            return;

        }

        int configurationStart = files.Count;

        AddCandidate(
            component,
            configuration,
            BackupArchivePaths.Configuration,
            files,
            accumulator,
            cancellationToken);

        if (files.Count != configurationStart + 1)
        {

            return;

        }

        bool stateExists = File.Exists(state);

        bool rollbackExists = File.Exists(rollback);

        if (!stateExists && !rollbackExists)
        {

            return;

        }

        if (!stateExists || !rollbackExists)
        {

            accumulator.Set(
                BackupComponentStatus.Failed,
                "Preset state and rollback must both exist before configuration backup.");

            return;

        }

        int sidecarStart = files.Count;

        AddCandidate(
            component,
            state,
            BackupArchivePaths.ConfigurationPresetState,
            files,
            accumulator,
            cancellationToken);

        AddCandidate(
            component,
            rollback,
            BackupArchivePaths.ConfigurationPresetRollback,
            files,
            accumulator,
            cancellationToken);

        bool pairCaptured = files.Count == sidecarStart + 2
            && string.Equals(
                files[sidecarStart].Sha256,
                files[sidecarStart + 1].Sha256,
                StringComparison.Ordinal);

        if (pairCaptured)
        {

            return;

        }

        for (int index = files.Count - 1; index >= sidecarStart; index--)
        {

            BackupInventoryFile removed = files[index];

            files.RemoveAt(index);

            accumulator.Remove(removed.Size);

        }

        accumulator.Set(
            BackupComponentStatus.Failed,
            "Preset state and rollback were linked, changed, or did not describe the same committed generation.");

    }

    private void AddTrustedMcpWorkspaceFiles(
        BackupComponent component,
        string primaryPath,
        IReadOnlySet<BackupComponent> selected,
        List<BackupInventoryFile> files,
        IReadOnlyDictionary<BackupComponent, ComponentAccumulator> components,
        CancellationToken cancellationToken)
    {

        if (!selected.Contains(component))
        {

            return;

        }

        if (!File.Exists(primaryPath))
        {

            components[component].Set(
                BackupComponentStatus.Unavailable,
                "No state exists for this optional component.");

            return;

        }

        AddCandidate(
            component,
            primaryPath,
            "mcp/trusted-workspaces.json",
            files,
            components[component],
            cancellationToken);

        string directory = Path.GetDirectoryName(primaryPath)!;

        for (long pageIndex = 1; ; pageIndex++)
        {

            string pageName = $"trusted-mcp-workspaces.page-{pageIndex:D8}.json";

            string pagePath = Path.Combine(directory, pageName);

            if (!File.Exists(pagePath))
            {

                return;

            }

            AddCandidate(
                component,
                pagePath,
                "mcp/" + pageName.Replace(
                    "trusted-mcp-workspaces",
                    "trusted-workspaces",
                    StringComparison.Ordinal),
                files,
                components[component],
                cancellationToken);

        }

    }

    private void AddOptionalFile(
        BackupComponent component,
        string source,
        string archivePath,
        IReadOnlySet<BackupComponent> selected,
        List<BackupInventoryFile> files,
        IReadOnlyDictionary<BackupComponent, ComponentAccumulator> components,
        CancellationToken cancellationToken)
    {

        if (!selected.Contains(component))
        {

            return;

        }

        if (!File.Exists(source))
        {

            components[component].Set(
                BackupComponentStatus.Unavailable,
                "No state exists for this optional component.");

            return;

        }

        AddCandidate(
            component,
            source,
            archivePath,
            files,
            components[component],
            cancellationToken);

    }

    private static void AddRequiredFile(
        BackupComponent component,
        string source,
        string archivePath,
        List<BackupInventoryFile> files,
        IReadOnlyDictionary<BackupComponent, ComponentAccumulator> components,
        List<string> missing,
        CancellationToken cancellationToken) =>
        _ = TryAddRequiredCandidate(
            component,
            source,
            archivePath,
            files,
            components[component],
            missing,
            cancellationToken);

    private static async Task<bool> TryAddRequiredBlobCandidateAsync(
        BackupComponent component,
        string source,
        string archivePath,
        EncryptedBlobPurpose purpose,
        int encryptionVersion,
        string? encryptionKeyId,
        List<BackupInventoryFile> files,
        ComponentAccumulator accumulator,
        List<string> missing,
        HashSet<string> requiredKeyIds,
        CancellationToken cancellationToken)
    {

        if (!File.Exists(source))
        {

            accumulator.Set(
                BackupComponentStatus.Failed,
                "A required database-backed blob is missing.");

            missing.Add(source);

            return false;

        }

        string fullPath = Path.GetFullPath(source);

        SecureFileOpenStatus status = SecureFileReader.TryOpenRegularFile(
            fullPath,
            expectedIdentity: null,
            out FileStream? stream,
            out FileHandleMetadata metadata);

        if (status != SecureFileOpenStatus.Success || stream is null)
        {

            accumulator.Set(
                BackupComponentStatus.Failed,
                "A selected database-backed blob is missing, linked, changed, or not an unaliased regular file.");

            accumulator.NonportablePaths.Add(fullPath);

            return false;

        }

        using (stream)
        {

            EncryptedBlobDescriptor? descriptorBeforeHash;

            try
            {

                descriptorBeforeHash = await BackupEncryptedBlobEnvelopeInspector.TryInspectAsync(
                        stream,
                        purpose,
                        cancellationToken)
                    .ConfigureAwait(false);

            }
            catch (Exception exception) when (
                exception is CryptographicException
                    or InvalidDataException
                    or IOException
                    or NotSupportedException
                    or UnauthorizedAccessException)
            {

                accumulator.Set(
                    BackupComponentStatus.Failed,
                    "A selected database-backed blob has an invalid ARCABLOB envelope or purpose: "
                        + exception.Message);

                return false;

            }

            try
            {

                BackupInventoryFile candidate = CaptureOpenedCandidate(
                    component,
                    fullPath,
                    archivePath,
                    stream,
                    metadata,
                    cancellationToken);

                stream.Position = 0;

                EncryptedBlobDescriptor? descriptorAfterHash = await BackupEncryptedBlobEnvelopeInspector
                    .TryInspectAsync(
                        stream,
                        purpose,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!EnvelopeDescriptorsMatch(
                        descriptorBeforeHash,
                        descriptorAfterHash))
                {

                    accumulator.Set(
                        BackupComponentStatus.Failed,
                        "A selected database-backed blob envelope changed while its exact archive bytes were fingerprinted.");

                    return false;

                }

                files.Add(candidate);

                accumulator.Add(candidate.Size);

                descriptorBeforeHash = descriptorAfterHash;

            }
            catch (Exception exception) when (
                exception is CryptographicException
                    or InvalidDataException
                    or IOException
                    or NotSupportedException
                    or UnauthorizedAccessException)
            {

                accumulator.Set(
                    BackupComponentStatus.Failed,
                    "A selected database-backed blob changed while its exact archive bytes were fingerprinted.");

                return false;

            }

            if (descriptorBeforeHash is not null)
            {

                _ = requiredKeyIds.Add(descriptorBeforeHash.KeyId);

                return true;

            }

            FailEncryptedMetadataWithoutEnvelope(
                encryptionVersion,
                encryptionKeyId,
                accumulator);

            return encryptionVersion <= 0;

        }

    }

    private static bool EnvelopeDescriptorsMatch(
        EncryptedBlobDescriptor? first,
        EncryptedBlobDescriptor? second)
    {

        if (first is null || second is null)
        {

            return first is null && second is null;

        }

        return first.Version == second.Version
            && first.Algorithm == second.Algorithm
            && first.ChunkSize == second.ChunkSize
            && string.Equals(first.KeyId, second.KeyId, StringComparison.Ordinal)
            && first.PlaintextLength == second.PlaintextLength
            && first.HeaderLength == second.HeaderLength
            && first.Purpose == second.Purpose
            && first.AuthenticatedMetadata.Span.SequenceEqual(
                second.AuthenticatedMetadata.Span);

    }

    private static bool TryAddRequiredCandidate(
        BackupComponent component,
        string source,
        string archivePath,
        List<BackupInventoryFile> files,
        ComponentAccumulator accumulator,
        List<string> missing,
        CancellationToken cancellationToken)
    {

        if (!File.Exists(source))
        {

            accumulator.Set(
                BackupComponentStatus.Failed,
                "A required manifest-listed source file is missing.");

            missing.Add(source);

            return false;

        }

        AddCandidate(
            component,
            source,
            archivePath,
            files,
            accumulator,
            cancellationToken);

        return true;

    }

    private static void AddCandidate(
        BackupComponent component,
        string source,
        string archivePath,
        List<BackupInventoryFile> files,
        ComponentAccumulator accumulator,
        CancellationToken cancellationToken)
    {

        string fullPath = Path.GetFullPath(source);

        SecureFileOpenStatus status = SecureFileReader.TryOpenRegularFile(
            fullPath,
            expectedIdentity: null,
            out FileStream? stream,
            out FileHandleMetadata metadata);

        if (status != SecureFileOpenStatus.Success || stream is null)
        {

            accumulator.Set(
                BackupComponentStatus.Failed,
                "A selected source is missing, linked, changed, or not an unaliased regular file.");

            accumulator.NonportablePaths.Add(fullPath);

            return;

        }

        using (stream)
        {

            BackupInventoryFile candidate = CaptureOpenedCandidate(
                component,
                fullPath,
                archivePath,
                stream,
                metadata,
                cancellationToken);

            files.Add(candidate);

            accumulator.Add(candidate.Size);

        }

    }

    private static BackupInventoryFile CaptureOpenedCandidate(
        BackupComponent component,
        string fullPath,
        string archivePath,
        FileStream stream,
        FileHandleMetadata metadata,
        CancellationToken cancellationToken)
    {

        long size = stream.Length;

        string sha256 = HashSource(
            stream,
            fullPath,
            metadata,
            size,
            cancellationToken);

        return new BackupInventoryFile(
            component,
            fullPath,
            archivePath.Normalize(NormalizationForm.FormC),
            size,
            sha256,
            metadata.Identity.VolumeId,
            metadata.Identity.FileId);

    }

    private static string HashSource(
        FileStream stream,
        string fullPath,
        FileHandleMetadata openedMetadata,
        long openedLength,
        CancellationToken cancellationToken)
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);

        try
        {

            while (true)
            {

                cancellationToken.ThrowIfCancellationRequested();

                int read = stream.Read(buffer);

                if (read == 0)
                {

                    break;

                }

                hash.AppendData(buffer.AsSpan(0, read));

            }

            if (stream.Length != openedLength
                || !SecureFileReader.TryValidateRegularFileHandle(
                    stream.SafeFileHandle,
                    openedMetadata.Identity,
                    out _)
                || !FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                    fullPath,
                    out FileHandleMetadata currentMetadata)
                || currentMetadata.Kind != FileSystemObjectKind.RegularFile
                || currentMetadata.HardLinkCount != 1
                || !FileHandleIdentity.IdentitiesMatch(
                    openedMetadata.Identity,
                    currentMetadata.Identity))
            {

                throw new IOException(
                    "A selected backup source changed while its inventory fingerprint was captured.");

            }

            byte[] digest = hash.GetHashAndReset();

            try
            {

                return Convert.ToHexString(digest).ToLowerInvariant();

            }
            finally
            {

                CryptographicOperations.ZeroMemory(digest);

            }

        }
        finally
        {

            CryptographicOperations.ZeroMemory(buffer);

            ArrayPool<byte>.Shared.Return(buffer);

        }

    }

    private void AddDirectoryTree(
        BackupComponent component,
        string sourceRoot,
        string archiveRoot,
        IReadOnlySet<BackupComponent> selected,
        List<BackupInventoryFile> files,
        IReadOnlyDictionary<BackupComponent, ComponentAccumulator> components,
        List<string> missing,
        CancellationToken cancellationToken)
    {

        if (!selected.Contains(component))
        {

            return;

        }

        if (!Directory.Exists(sourceRoot))
        {

            components[component].Set(
                BackupComponentStatus.Unavailable,
                "No state exists for this optional component.");

            return;

        }

        Stack<string> directories = new();

        directories.Push(Path.GetFullPath(sourceRoot));

        while (directories.Count > 0)
        {

            string directory = directories.Pop();

            DirectoryInfo directoryInfo = new(directory);

            if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {

                components[component].Set(
                    BackupComponentStatus.Failed,
                    "A selected directory tree contains a symbolic link or reparse point.");

                components[component].NonportablePaths.Add(directory);

                continue;

            }

            foreach (string childDirectory in Directory.EnumerateDirectories(directory))
            {

                directories.Push(childDirectory);

            }

            foreach (string file in Directory.EnumerateFiles(directory))
            {

                string relative = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');

                AddCandidate(
                    component,
                    file,
                    archiveRoot + "/" + relative,
                    files,
                    components[component],
                    cancellationToken);

            }

        }

        if (components[component].Files == 0
            && components[component].Status == BackupComponentStatus.Complete)
        {

            components[component].Set(
                BackupComponentStatus.Unavailable,
                "The optional component directory is empty.");

        }

    }

    private static void AddDatedLogs(
        BackupComponent component,
        string basePath,
        IReadOnlySet<BackupComponent> selected,
        List<BackupInventoryFile> files,
        IReadOnlyDictionary<BackupComponent, ComponentAccumulator> components,
        CancellationToken cancellationToken)
    {

        if (!selected.Contains(component))
        {

            return;

        }

        string fullBasePath = Path.GetFullPath(basePath);

        string directory = Path.GetDirectoryName(fullBasePath) ?? string.Empty;

        string stem = Path.GetFileNameWithoutExtension(fullBasePath);

        if (!Directory.Exists(directory))
        {

            components[component].Set(
                BackupComponentStatus.Unavailable,
                "No selected dated logs exist.");

            return;

        }

        string[] candidates = Directory
            .EnumerateFiles(directory, stem + "-*.jsonl", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (string candidate in candidates)
        {

            AddCandidate(
                component,
                candidate,
                "logs/" + Path.GetFileName(candidate),
                files,
                components[component],
                cancellationToken);

        }

        if (candidates.Length == 0)
        {

            components[component].Set(
                BackupComponentStatus.Unavailable,
                "No selected dated logs exist.");

        }

    }

    private static bool TryResolveContainedPath(
        string root,
        string relative,
        out string fullPath)
    {

        fullPath = string.Empty;

        try
        {

            if (Path.IsPathRooted(relative)
                || relative.Contains('\0')
                || relative.Split('/').Any(static segment => segment is "" or "." or ".."))
            {

                return false;

            }

            string fullRoot = Path.GetFullPath(root);

            fullPath = Path.GetFullPath(
                Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

            string prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;

            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!fullPath.StartsWith(prefix, comparison))
            {

                return false;

            }

            if (!IsPathComponentPortable(fullRoot))
            {

                return false;

            }

            string current = fullRoot;

            foreach (string segment in relative.Split('/'))
            {

                current = Path.Combine(current, segment);

                if (!IsPathComponentPortable(current))
                {

                    return false;

                }

            }

            return true;

        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or PathTooLongException
                or UnauthorizedAccessException)
        {

            return false;

        }

    }

    private static bool IsPathComponentPortable(string path)
    {

        try
        {

            FileAttributes attributes = File.GetAttributes(path);

            return (attributes & FileAttributes.ReparsePoint) == 0;

        }
        catch (FileNotFoundException)
        {

            return true;

        }
        catch (DirectoryNotFoundException)
        {

            return true;

        }
        catch (Exception exception) when (
            exception is IOException
                or NotSupportedException
                or UnauthorizedAccessException)
        {

            return false;

        }

    }

    private static void EnsureUniqueArchivePaths(IReadOnlyList<BackupInventoryFile> files)
    {

        HashSet<string> paths = new(StringComparer.Ordinal);

        foreach (BackupInventoryFile file in files)
        {

            if (!paths.Add(file.ArchivePath.Normalize(NormalizationForm.FormC)))
            {

                throw new InvalidDataException("Backup inventory contains a duplicate canonical archive path.");

            }

        }

    }

    private static string BuildConnectionString(string path, string passphrase)
    {

        SqliteConnectionStringBuilder builder = new()
        {

            DataSource = path,

            Mode = SqliteOpenMode.ReadOnly,

            Pooling = false,

        };

        if (!string.IsNullOrEmpty(passphrase))
        {

            builder.Password = passphrase;

        }

        return builder.ToString();

    }

    private sealed class ComponentAccumulator(
        BackupComponent component,
        BackupComponentStatus status,
        string detail)
    {

        public BackupComponent Component { get; } = component;

        public BackupComponentStatus Status { get; private set; } = status;

        public string Detail { get; private set; } = detail;

        public long Files { get; private set; }

        public long Bytes { get; private set; }

        public List<string> NonportablePaths { get; } = [];

        public void Add(long bytes)
        {

            Files++;

            Bytes = checked(Bytes + bytes);

            if (Status == BackupComponentStatus.Complete)
            {

                Detail = "Included through the typed component catalog.";

            }

        }

        public void Remove(long bytes)
        {

            Files = checked(Files - 1);

            Bytes = checked(Bytes - bytes);

        }

        public void Set(BackupComponentStatus next, string nextDetail)
        {

            if (Status == BackupComponentStatus.Failed
                && next != BackupComponentStatus.Failed)
            {

                return;

            }

            Status = next;

            Detail = nextDetail;

        }

        public BackupPlanComponent ToPlanComponent() =>
            new(
                Component,
                Status,
                Detail,
                Files,
                Bytes,
                [.. NonportablePaths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);

    }

}
