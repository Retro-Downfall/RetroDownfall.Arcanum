using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Storage;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed class BackupInventoryPlannerTests : IDisposable
{
    /// <summary>
    /// The native provider has to be installed before the first connection is constructed. Doing it
    /// here rather than relying on some earlier suite having done it keeps this class from passing or
    /// failing according to the order the runner happened to pick.
    /// </summary>
    static BackupInventoryPlannerTests() => SqliteNativeRuntime.Instance.Initialize();

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-backup-inventory-" + Guid.NewGuid().ToString("N"));

    private readonly string _databasePath;

    public BackupInventoryPlannerTests()
    {
        Directory.CreateDirectory(_root);

        _databasePath = Path.Combine(_root, "arcanum.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Full_inventory_deduplicates_batch_files_and_fails_a_missing_attachment_reference()
    {
        await CreateInventoryDatabaseAsync();

        string files = Path.Combine(_root, "files");

        Directory.CreateDirectory(files);

        Guid ordinaryUpload = Guid.Parse("11111111-1111-1111-1111-111111111111");

        Guid batchInput = Guid.Parse("22222222-2222-2222-2222-222222222222");

        await File.WriteAllTextAsync(Path.Combine(files, ordinaryUpload.ToString("N")), "upload");

        await File.WriteAllTextAsync(Path.Combine(files, batchInput.ToString("N")), "batch");

        BackupInventoryPlanner planner = new(Paths());

        BackupInventory inventory = await planner.BuildAsync(
            new BackupPlanRequest(BackupScope.Full, null, [], []),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        Assert.Contains(
            inventory.Plan.Components,
            item => item.Component == BackupComponent.SessionAttachments
                && item.Status == BackupComponentStatus.Failed);

        Assert.Single(
            inventory.Files,
            file => file.Component == BackupComponent.UploadedFiles);

        Assert.Single(
            inventory.Files,
            file => file.Component == BackupComponent.BatchArtifacts);

        Assert.Equal(
            inventory.Files.Select(static file => file.ArchivePath).Distinct(StringComparer.Ordinal).Count(),
            inventory.Files.Count);

        Assert.Empty(inventory.RequiredFileEncryptionKeyIds);
    }

    [Fact]
    public async Task Explicit_typed_exclusions_are_reported_without_hiding_other_components()
    {
        await CreateInventoryDatabaseAsync();

        BackupInventoryPlanner planner = new(Paths());

        BackupInventory inventory = await planner.BuildAsync(
            new BackupPlanRequest(
                BackupScope.Full,
                null,
                Include: [BackupComponent.AuditLogs],
                Exclude: [BackupComponent.GlobalSpells]),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        Assert.Contains(
            inventory.Plan.Components,
            item => item.Component == BackupComponent.GlobalSpells
                && item.Status == BackupComponentStatus.OmittedByPolicy);

        Assert.Contains(
            inventory.Plan.Components,
            item => item.Component == BackupComponent.AuditLogs
                && item.Status != BackupComponentStatus.OmittedByPolicy);

        Assert.Contains(
            inventory.Plan.SecurityWarnings,
            warning => warning.Contains("audit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Specific_session_selects_only_its_attachments_and_discloses_database_collateral()
    {
        Guid targetSession = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        Guid otherSession = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        Guid ordinaryUpload = Guid.Parse("11111111-1111-1111-1111-111111111111");

        Guid batchInput = Guid.Parse("22222222-2222-2222-2222-222222222222");

        await CreateInventoryDatabaseAsync(
            $$"""
            INSERT INTO SessionAttachments
                (Id, SessionId, RelativePath, State, EncryptionVersion, EncryptionKeyId)
            VALUES
                ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',
                 '{{Canonical(targetSession)}}',
                 'target/attachment.bin',
                 'Bound',
                 0,
                 NULL),
                ('DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD',
                 '{{Canonical(otherSession)}}',
                 'other/attachment.bin',
                 'Bound',
                 0,
                 NULL);

            INSERT INTO UploadedFiles (Id, EncryptionVersion, EncryptionKeyId)
            VALUES
                ('{{ordinaryUpload:D}}', 0, NULL),
                ('{{batchInput:D}}', 0, NULL);

            INSERT INTO Batches (Id, InputFileId, OutputFileId, ErrorFileId)
            VALUES
                ('33333333-3333-3333-3333-333333333333',
                 '{{batchInput:D}}',
                 NULL,
                 NULL);
            """);

        await WriteFileAsync("attachments/target/attachment.bin", "target");

        await WriteFileAsync("attachments/other/attachment.bin", "other");

        await WriteFileAsync("files/" + ordinaryUpload.ToString("N"), "upload");

        await WriteFileAsync("files/" + batchInput.ToString("N"), "batch");

        await File.WriteAllTextAsync(_databasePath + ".kdf", "{}");

        BackupInventoryPlanner planner = new(Paths());

        BackupInventory inventory = await planner.BuildAsync(
            new BackupPlanRequest(
                BackupScope.SpecificSession,
                targetSession,
                Include: [],
                Exclude: []),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        BackupInventoryFile attachment = Assert.Single(
            inventory.Files,
            file => file.Component == BackupComponent.SessionAttachments);

        Assert.Equal("attachments/target/attachment.bin", attachment.ArchivePath);

        Assert.DoesNotContain(
            inventory.Files,
            file => file.Component is BackupComponent.UploadedFiles
                or BackupComponent.BatchArtifacts);

        Assert.Empty(inventory.RequiredFileEncryptionKeyIds);

        Assert.Contains(
            inventory.Plan.Components,
            component => component.Component == BackupComponent.UploadedFiles
                && component.Status == BackupComponentStatus.OmittedByPolicy);

        Assert.Contains(
            inventory.Plan.Components,
            component => component.Component == BackupComponent.BatchArtifacts
                && component.Status == BackupComponentStatus.OmittedByPolicy);

        Assert.Contains(
            inventory.Plan.SecurityWarnings,
            warning => warning.Contains("collateral", StringComparison.OrdinalIgnoreCase)
                && warning.Contains("indivisible", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Specific_session_uses_the_key_id_from_ciphertext_replaced_before_snapshot_metadata()
    {
        Guid targetSession = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        await CreateInventoryDatabaseAsync(
            $$"""
            INSERT INTO SessionAttachments
                (Id, SessionId, RelativePath, State, EncryptionVersion, EncryptionKeyId)
            VALUES
                ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',
                 '{{Canonical(targetSession)}}',
                 'target/attachment.bin',
                 'Bound',
                 1,
                 'snapshot-key');
            """);

        string attachmentPath = Path.Combine(
            _root,
            "attachments",
            "target",
            "attachment.bin");

        EncryptedBlobStore blobStore = TestEncryptedBlobStore.Create();

        await using MemoryStream plaintext = new("replacement ciphertext"u8.ToArray());

        EncryptedBlobDescriptor descriptor = await blobStore.WriteAsync(
            attachmentPath,
            plaintext,
            EncryptedBlobPurpose.SessionAttachment,
            plaintextLength: plaintext.Length);

        BackupInventoryPlanner planner = new(Paths());

        BackupInventory inventory = await planner.BuildAsync(
            new BackupPlanRequest(
                BackupScope.SpecificSession,
                targetSession,
                Include: [],
                Exclude: []),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        Assert.Equal(
            [descriptor.KeyId],
            inventory.RequiredFileEncryptionKeyIds.Order(StringComparer.Ordinal));

        Assert.DoesNotContain(
            "snapshot-key",
            inventory.RequiredFileEncryptionKeyIds);

        Assert.Contains(
            inventory.Plan.Components,
            component => component.Component == BackupComponent.SessionAttachments
                && component.Status == BackupComponentStatus.Complete);
    }

    [Fact]
    public async Task Batch_input_uses_its_uploaded_file_envelope_purpose_and_actual_key()
    {
        Guid batchInput = Guid.Parse("22222222-2222-2222-2222-222222222222");

        await CreateInventoryDatabaseAsync(
            $$"""
            INSERT INTO UploadedFiles
                (Id, Purpose, EncryptionVersion, EncryptionKeyId)
            VALUES
                ('{{batchInput:D}}', 'batch', 1, 'snapshot-key');

            INSERT INTO Batches (Id, InputFileId, OutputFileId, ErrorFileId)
            VALUES
                ('33333333-3333-3333-3333-333333333333',
                 '{{batchInput:D}}',
                 NULL,
                 NULL);
            """);

        string inputPath = Path.Combine(
            _root,
            "files",
            batchInput.ToString("N"));

        EncryptedBlobStore blobStore = TestEncryptedBlobStore.Create();

        await using MemoryStream plaintext = new("batch input"u8.ToArray());

        EncryptedBlobDescriptor descriptor = await blobStore.WriteAsync(
            inputPath,
            plaintext,
            EncryptedBlobPurpose.UploadedFile,
            plaintextLength: plaintext.Length);

        BackupInventoryPlanner planner = new(Paths());

        BackupInventory inventory = await planner.BuildAsync(
            new BackupPlanRequest(
                BackupScope.MetadataOnly,
                SessionId: null,
                Include: [BackupComponent.BatchArtifacts],
                Exclude: []),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        Assert.Equal(
            [descriptor.KeyId],
            inventory.RequiredFileEncryptionKeyIds.Order(StringComparer.Ordinal));

        Assert.Contains(
            inventory.Plan.Components,
            component => component.Component == BackupComponent.BatchArtifacts
                && component.Status == BackupComponentStatus.Complete);
    }

    [Fact]
    public async Task Malformed_encrypted_blob_header_fails_its_component_without_requiring_snapshot_key()
    {
        await CreateInventoryDatabaseAsync(
            """
            INSERT INTO SessionAttachments
                (Id, SessionId, RelativePath, State, EncryptionVersion, EncryptionKeyId)
            VALUES
                ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',
                 'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',
                 'malformed/attachment.bin',
                 'Bound',
                 1,
                 'snapshot-key');
            """);

        await WriteFileAsync(
            "attachments/malformed/attachment.bin",
            "ARCABLOB");

        BackupInventoryPlanner planner = new(Paths());

        BackupInventory inventory = await planner.BuildAsync(
            new BackupPlanRequest(
                BackupScope.MetadataOnly,
                SessionId: null,
                Include: [BackupComponent.SessionAttachments],
                Exclude: []),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        Assert.Empty(inventory.RequiredFileEncryptionKeyIds);

        Assert.Contains(
            inventory.Plan.Components,
            component => component.Component == BackupComponent.SessionAttachments
                && component.Status == BackupComponentStatus.Failed
                && component.Detail.Contains(
                    "header",
                    StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Envelope_purpose_mismatch_fails_its_owning_component()
    {
        await CreateInventoryDatabaseAsync(
            """
            INSERT INTO SessionAttachments
                (Id, SessionId, RelativePath, State, EncryptionVersion, EncryptionKeyId)
            VALUES
                ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',
                 'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',
                 'wrong-purpose/attachment.bin',
                 'Bound',
                 1,
                 'snapshot-key');
            """);

        string attachmentPath = Path.Combine(
            _root,
            "attachments",
            "wrong-purpose",
            "attachment.bin");

        EncryptedBlobStore blobStore = TestEncryptedBlobStore.Create();

        await using MemoryStream plaintext = new("wrong purpose"u8.ToArray());

        _ = await blobStore.WriteAsync(
            attachmentPath,
            plaintext,
            EncryptedBlobPurpose.UploadedFile,
            plaintextLength: plaintext.Length);

        BackupInventoryPlanner planner = new(Paths());

        BackupInventory inventory = await planner.BuildAsync(
            new BackupPlanRequest(
                BackupScope.MetadataOnly,
                SessionId: null,
                Include: [BackupComponent.SessionAttachments],
                Exclude: []),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        Assert.Empty(inventory.RequiredFileEncryptionKeyIds);

        Assert.Contains(
            inventory.Plan.Components,
            component => component.Component == BackupComponent.SessionAttachments
                && component.Status == BackupComponentStatus.Failed
                && component.Detail.Contains(
                    "purpose",
                    StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task Database_backed_file_under_symlinked_parent_is_rejected()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "This asserts POSIX behaviour that Windows does not model.");

        await CreateInventoryDatabaseAsync(
            """
            INSERT INTO SessionAttachments
                (Id, SessionId, RelativePath, State, EncryptionVersion, EncryptionKeyId)
            VALUES
                ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',
                 'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',
                 'linked/attachment.bin',
                 'Bound',
                 1,
                 'attachment-key');
            """);

        string outside = Path.Combine(_root, "outside");

        Directory.CreateDirectory(outside);

        await File.WriteAllTextAsync(
            Path.Combine(outside, "attachment.bin"),
            "outside bytes");

        Directory.CreateDirectory(Path.Combine(_root, "attachments"));

        Directory.CreateSymbolicLink(
            Path.Combine(_root, "attachments", "linked"),
            outside);

        BackupInventoryPlanner planner = new(Paths());

        BackupInventory inventory = await planner.BuildAsync(
            new BackupPlanRequest(
                BackupScope.MetadataOnly,
                SessionId: null,
                Include: [BackupComponent.SessionAttachments],
                Exclude: []),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        Assert.DoesNotContain(
            inventory.Files,
            file => file.Component == BackupComponent.SessionAttachments);

        Assert.Contains(
            inventory.Plan.Components,
            component => component.Component == BackupComponent.SessionAttachments
                && component.Status == BackupComponentStatus.Failed
                && component.NonportablePaths.Length == 1);
    }

    [Fact]
    public async Task Exact_typed_selection_produces_deterministically_ordered_inventory()
    {
        await CreateInventoryDatabaseAsync(seedSql: null);

        await WriteFileAsync("spells/zeta/SPELL.md", "zeta");

        await WriteFileAsync("spells/alpha/SPELL.md", "alpha");

        await WriteFileAsync("arcanum.json", "{}");

        await WriteFileAsync("audit-20260802.jsonl", "{}\n");

        BackupPlanRequest request = new(
            BackupScope.MetadataOnly,
            SessionId: null,
            Include:
            [
                BackupComponent.GlobalSpells,
                BackupComponent.Configuration,
                BackupComponent.AuditLogs,
            ],
            Exclude: [BackupComponent.GlobalCodex]);

        BackupInventoryPlanner planner = new(Paths());

        BackupInventory first = await planner.BuildAsync(
            request,
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        BackupInventory second = await planner.BuildAsync(
            request,
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        string[] firstPaths = [.. first.Files.Select(static file => file.ArchivePath)];

        string[] secondPaths = [.. second.Files.Select(static file => file.ArchivePath)];

        Assert.Equal(firstPaths.Order(StringComparer.Ordinal), firstPaths);

        Assert.Equal(firstPaths, secondPaths);

        Assert.Equal(
            request.Include.Order(),
            first.Plan.Components
                .Where(static component => component.Status != BackupComponentStatus.OmittedByPolicy)
                .Select(static component => component.Component)
                .Order());
    }

    /// <summary>
    /// Two files whose names differ only in case are two archive entries and one file on a
    /// case-insensitive volume, so no restore target can lay both down; the planner fails the
    /// component instead of publishing an archive that verifies but cannot be restored.
    /// </summary>
    [SkippableFact]
    public async Task A_selected_tree_with_names_differing_only_in_case_is_a_failed_component()
    {
        await CreateInventoryDatabaseAsync(seedSql: null);

        await WriteFileAsync("spells/Notes/SPELL.md", "upper");

        await WriteFileAsync("spells/notes/SPELL.md", "lower");

        Skip.If(
            Directory.GetDirectories(Path.Combine(_root, "spells")).Length < 2,
            "This volume folds case, so the two names cannot coexist to be planned.");

        await AssertCollidingSpellTreeFailsAsync();
    }

    /// <summary>
    /// Windows silently drops trailing dots and spaces from a name, so <c>a.md.</c> and <c>a.md</c>
    /// are the same destination there. Unlike a case collision this one can be built on every
    /// volume the suite runs on other than Windows.
    /// </summary>
    [SkippableFact]
    public async Task A_selected_tree_with_names_differing_only_in_a_trailing_dot_is_a_failed_component()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Windows cannot create the second name; this builds the collision on a POSIX volume.");

        await CreateInventoryDatabaseAsync(seedSql: null);

        await WriteFileAsync("spells/alpha/SPELL.md", "plain");

        await WriteFileAsync("spells/alpha/SPELL.md.", "dotted");

        await AssertCollidingSpellTreeFailsAsync();
    }

    [SkippableFact]
    public async Task A_colliding_pair_is_removed_but_unrelated_files_in_the_same_component_remain()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Windows cannot create the colliding trailing-dot name.");

        await CreateInventoryDatabaseAsync(seedSql: null);

        await WriteFileAsync("spells/alpha/SPELL.md", "plain");

        await WriteFileAsync("spells/alpha/SPELL.md ", "spaced");

        await WriteFileAsync("spells/beta/SPELL.md", "beta");

        BackupInventory inventory = await PlanGlobalSpellsAsync();

        BackupPlanComponent component = Assert.Single(
            inventory.Plan.Components,
            static item => item.Component == BackupComponent.GlobalSpells);

        Assert.Equal(BackupComponentStatus.Failed, component.Status);

        Assert.Equal(
            ["authored/spells/beta/SPELL.md"],
            inventory.Files.Select(static file => file.ArchivePath));

        Assert.Equal(1, component.Files);

        Assert.Equal(inventory.Files.Sum(static file => file.Size), component.EstimatedBytes);
    }

    [Theory]
    [InlineData("authored/spells/Notes/SPELL.md", "authored/spells/notes/spell.md")]
    [InlineData("authored/spells/a/SPELL.md.", "authored/spells/a/SPELL.md")]
    [InlineData("authored/spells/a./SPELL.md", "authored/spells/a/SPELL.md")]
    [InlineData("authored/spells/a/SPELL.md  ", "authored/spells/a/SPELL.md")]
    public void Archive_paths_that_differ_only_in_case_or_trailing_dots_and_spaces_share_a_collision_key(
        string first,
        string second)
    {
        Assert.Equal(
            BackupArchivePathFolding.CollisionKey(first),
            BackupArchivePathFolding.CollisionKey(second),
            BackupArchivePathFolding.KeyComparer);
    }

    [Theory]
    [InlineData("authored/spells/a/SPELL.md", "authored/spells/b/SPELL.md")]
    [InlineData("authored/spells/a/SPELL.md", "authored/spells/a/SPELL.mdx")]
    [InlineData("authored/spells/a.b/SPELL.md", "authored/spells/ab/SPELL.md")]
    [InlineData("authored/spells/ a/SPELL.md", "authored/spells/a/SPELL.md")]
    public void Archive_paths_that_name_different_destinations_do_not_share_a_collision_key(
        string first,
        string second)
    {
        Assert.NotEqual(
            BackupArchivePathFolding.CollisionKey(first),
            BackupArchivePathFolding.CollisionKey(second),
            BackupArchivePathFolding.KeyComparer);
    }

    /// <summary>
    /// A file and a directory cannot share a name on a destination that ignores case and trailing dots,
    /// so a file named like another file's directory is the same collision as two files with one name.
    /// </summary>
    [SkippableFact]
    public async Task A_file_named_like_another_files_directory_is_a_failed_component()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Windows cannot create the trailing-dot name; this builds the collision on a POSIX volume.");

        await CreateInventoryDatabaseAsync(seedSql: null);

        await WriteFileAsync("spells/alpha/SPELL.md", "unrelated");

        await WriteFileAsync("spells/beta.", "a file whose name Windows would fold onto the directory below");

        await WriteFileAsync("spells/beta/SPELL.md", "inside the directory");

        BackupInventory inventory = await PlanGlobalSpellsAsync();

        BackupPlanComponent component = Assert.Single(
            inventory.Plan.Components,
            static item => item.Component == BackupComponent.GlobalSpells);

        Assert.Equal(BackupComponentStatus.Failed, component.Status);

        // The file and everything under the directory it collides with are withdrawn; the unrelated
        // sibling stays.
        Assert.Equal(
            ["authored/spells/alpha/SPELL.md"],
            inventory.Files.Select(static file => file.ArchivePath));

        Assert.Equal(2, component.NonportablePaths.Length);
    }

    [Theory]
    [InlineData(new[] { "a/Notes/SPELL.md", "a/notes/SPELL.md", "a/other.md" }, new[] { 0, 1 })]
    [InlineData(new[] { "a/SPELL.md.", "a/SPELL.md" }, new[] { 0, 1 })]
    [InlineData(new[] { "a", "A/x", "A/y", "b" }, new[] { 0, 1, 2 })]
    [InlineData(new[] { "x/a.", "x/A/y", "x/b" }, new[] { 0, 1 })]
    [InlineData(new[] { "dup", "dup" }, new[] { 0, 1 })]
    [InlineData(new[] { "A/x", "a/y" }, new int[0])]
    [InlineData(new[] { "a.b", "a/b" }, new int[0])]
    [InlineData(new[] { "ab", "a/x" }, new int[0])]
    [InlineData(new[] { "a/b/c", "a/b", "a/d" }, new[] { 0, 1 })]
    public void Colliding_archive_paths_are_found_by_case_trailing_dots_and_file_versus_directory(
        string[] archivePaths,
        int[] expectedMembers)
    {
        HashSet<int> colliding = BackupArchivePathFolding.FindCollidingIndexes(archivePaths);

        Assert.Equal(
            expectedMembers.Order(),
            colliding.Order());
    }

    /// <summary>
    /// The planner's file-system-independent counterpart of the case-only test above, which cannot
    /// build its two names on a volume that folds case: the same shared fold, fed the case pair the
    /// real tree would have held.
    /// </summary>
    [Fact]
    public void A_case_only_collision_fails_the_owning_component_without_a_case_sensitive_volume()
    {
        Dictionary<BackupComponent, BackupInventoryPlanner.ComponentAccumulator> components = Accumulators();

        List<BackupInventoryFile> files =
        [
            InventoryFile(BackupComponent.GlobalSpells, "authored/spells/Notes/SPELL.md"),

            InventoryFile(BackupComponent.GlobalSpells, "authored/spells/notes/SPELL.md"),

            InventoryFile(BackupComponent.GlobalSpells, "authored/spells/other/SPELL.md"),
        ];

        Resolve(
            new HashSet<BackupComponent> { BackupComponent.GlobalSpells },
            files,
            components);

        BackupPlanComponent component = components[BackupComponent.GlobalSpells].ToPlanComponent();

        Assert.Equal(BackupComponentStatus.Failed, component.Status);

        Assert.Equal(
            ["authored/spells/other/SPELL.md"],
            files.Select(static file => file.ArchivePath));

        Assert.Equal(1, component.Files);

        Assert.Equal(2, component.NonportablePaths.Length);
    }

    /// <summary>
    /// A component that already failed for another reason keeps saying why: the collision adds its own
    /// reason instead of replacing the first one with the last.
    /// </summary>
    [Fact]
    public void A_collision_adds_to_an_earlier_failure_detail_instead_of_replacing_it()
    {
        Dictionary<BackupComponent, BackupInventoryPlanner.ComponentAccumulator> components = Accumulators();

        components[BackupComponent.GlobalSpells].Set(
            BackupComponentStatus.Failed,
            "A required file is a symbolic link.");

        List<BackupInventoryFile> files =
        [
            InventoryFile(BackupComponent.GlobalSpells, "authored/spells/a/SPELL.md"),

            InventoryFile(BackupComponent.GlobalSpells, "authored/spells/a/SPELL.md."),

            InventoryFile(BackupComponent.GlobalSpells, "authored/spells/b/SPELL.md"),

            InventoryFile(BackupComponent.GlobalSpells, "authored/spells/b/SPELL.md "),
        ];

        Resolve(
            new HashSet<BackupComponent> { BackupComponent.GlobalSpells },
            files,
            components);

        BackupPlanComponent component = components[BackupComponent.GlobalSpells].ToPlanComponent();

        Assert.Equal(BackupComponentStatus.Failed, component.Status);

        Assert.Contains("symbolic link", component.Detail, StringComparison.Ordinal);

        // Four withdrawn files in two colliding groups, one explanation of the collision.
        Assert.Equal(
            1,
            component.Detail.Split("collide").Length - 1);
    }

    /// <summary>
    /// The Compendium alias reports whatever the Configuration entry it shares ended up as, and that
    /// includes the collision pass, which runs last.
    /// </summary>
    [Fact]
    public void The_compendium_alias_follows_the_configuration_status_after_the_collision_pass()
    {
        Dictionary<BackupComponent, BackupInventoryPlanner.ComponentAccumulator> components = Accumulators();

        List<BackupInventoryFile> files =
        [
            InventoryFile(BackupComponent.Configuration, "configuration/arcanum.json"),

            InventoryFile(BackupComponent.Configuration, "configuration/ARCANUM.json"),
        ];

        Resolve(
            new HashSet<BackupComponent>
            {
                BackupComponent.Configuration,

                BackupComponent.CompendiumSettings,
            },
            files,
            components);

        Assert.Equal(
            BackupComponentStatus.Failed,
            components[BackupComponent.Configuration].Status);

        Assert.Equal(
            BackupComponentStatus.Failed,
            components[BackupComponent.CompendiumSettings].Status);
    }

    /// <summary>
    /// Windows drops trailing dots and spaces from a name, which is the premise of the fold; this runs
    /// where that is true instead of assuming it from the other platforms.
    /// </summary>
    [SkippableFact]
    public async Task Windows_treats_a_trailing_dot_name_as_the_same_destination()
    {
        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Windows lane only: the platform that folds trailing dots and spaces is the one this asserts about.");

        await WriteFileAsync("spells/alpha/SPELL.md", "first");

        await WriteFileAsync("spells/alpha/SPELL.md.", "second");

        string[] names = Directory.GetFiles(Path.Combine(_root, "spells", "alpha"));

        string only = Assert.Single(names);

        Assert.Equal("second", await File.ReadAllTextAsync(only));
    }

    private async Task AssertCollidingSpellTreeFailsAsync()
    {
        BackupInventory inventory = await PlanGlobalSpellsAsync();

        BackupPlanComponent component = Assert.Single(
            inventory.Plan.Components,
            static item => item.Component == BackupComponent.GlobalSpells);

        Assert.Equal(BackupComponentStatus.Failed, component.Status);

        Assert.Equal(2, component.NonportablePaths.Length);

        Assert.Empty(inventory.Files);
    }

    private Task<BackupInventory> PlanGlobalSpellsAsync() =>
        new BackupInventoryPlanner(Paths()).BuildAsync(
            new BackupPlanRequest(
                BackupScope.MetadataOnly,
                SessionId: null,
                Include: [BackupComponent.GlobalSpells],
                Exclude: []),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

    [Fact]
    public async Task A_plan_that_carries_recovery_keys_warns_about_the_passphrase_minimum()
    {
        await CreateInventoryDatabaseAsync(seedSql: null);

        BackupInventoryPlanner planner = new(Paths());

        BackupInventory withKeys = await planner.BuildAsync(
            new BackupPlanRequest(
                BackupScope.MetadataOnly,
                SessionId: null,
                Include: [BackupComponent.PortableRecoveryKeys],
                Exclude: []),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        Assert.Contains(BackupPassphrasePolicy.CreateWarning, withKeys.Plan.SecurityWarnings);

        BackupInventory withoutKeys = await planner.BuildAsync(
            new BackupPlanRequest(
                BackupScope.MetadataOnly,
                SessionId: null,
                Include: [],
                Exclude: []),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        Assert.DoesNotContain(BackupPassphrasePolicy.CreateWarning, withoutKeys.Plan.SecurityWarnings);
    }

    [Fact]
    public async Task Configuration_inventory_includes_committed_preset_state_and_rollback()
    {
        await WriteFileAsync("arcanum.json", "{}");

        await WriteFileAsync("arcanum.preset.json", "{\"presetId\":\"research\"}");

        await WriteFileAsync(
            "arcanum.preset.rollback.json",
            "{\"presetId\":\"research\"}");

        BackupInventoryPlanner planner = new(Paths());

        BackupInventory inventory = await planner.BuildAsync(
            new BackupPlanRequest(
                BackupScope.MetadataOnly,
                SessionId: null,
                Include: [BackupComponent.Configuration],
                Exclude: []),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        Assert.Equal(
            [
                "configuration/arcanum.json",
                "configuration/arcanum.preset.json",
                "configuration/arcanum.preset.rollback.json",
            ],
            inventory.Files.Select(static file => file.ArchivePath));

        Assert.All(
            inventory.Files,
            static file => Assert.Equal(BackupComponent.Configuration, file.Component));

        Assert.Contains(
            inventory.Plan.Components,
            component => component.Component == BackupComponent.Configuration
                && component.Status == BackupComponentStatus.Complete
                && component.Files == 3);
    }

    [Fact]
    public async Task Configuration_inventory_never_includes_the_transient_preset_journal()
    {
        await WriteFileAsync("arcanum.json", "{}");

        await WriteFileAsync(
            "arcanum.preset.journal.json",
            "{\"operation\":\"apply\"}");

        await WriteFileAsync("arcanum.preset.json", "{\"presetId\":\"research\"}");

        await WriteFileAsync(
            "arcanum.preset.rollback.json",
            "{\"presetId\":\"research\"}");

        BackupInventoryPlanner planner = new(Paths());

        BackupInventory inventory = await planner.BuildAsync(
            new BackupPlanRequest(
                BackupScope.MetadataOnly,
                SessionId: null,
                Include: [BackupComponent.Configuration],
                Exclude: []),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        Assert.Empty(inventory.Files);

        Assert.DoesNotContain(
            inventory.Files,
            static file => file.ArchivePath.Contains("journal", StringComparison.Ordinal));

        Assert.Contains(
            inventory.Plan.Components,
            component => component.Component == BackupComponent.Configuration
                && component.Status == BackupComponentStatus.Failed
                && component.Files == 0);
    }

    [Fact]
    public async Task Configuration_inventory_rejects_mismatched_preset_state_and_rollback()
    {
        await WriteFileAsync("arcanum.json", "{}");

        await WriteFileAsync("arcanum.preset.json", "{\"presetId\":\"research\"}");

        await WriteFileAsync(
            "arcanum.preset.rollback.json",
            "{\"presetId\":\"general-assistant\"}");

        BackupInventoryPlanner planner = new(Paths());

        BackupInventory inventory = await planner.BuildAsync(
            new BackupPlanRequest(
                BackupScope.MetadataOnly,
                SessionId: null,
                Include: [BackupComponent.Configuration],
                Exclude: []),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        BackupInventoryFile configuration = Assert.Single(inventory.Files);

        Assert.Equal("configuration/arcanum.json", configuration.ArchivePath);

        Assert.Contains(
            inventory.Plan.Components,
            component => component.Component == BackupComponent.Configuration
                && component.Status == BackupComponentStatus.Failed
                && component.Files == 1);
    }

    [Fact]
    public async Task Configuration_inventory_never_captures_sidecars_without_a_regular_config_file()
    {
        string target = Path.Combine(_root, "configuration-target.json");

        await File.WriteAllTextAsync(target, "{}");

        Assert.True(HardLinkTestSupport.TryCreate(
            Path.Combine(_root, "arcanum.json"),
            target));

        await WriteFileAsync("arcanum.preset.json", "{\"presetId\":\"research\"}");

        await WriteFileAsync(
            "arcanum.preset.rollback.json",
            "{\"presetId\":\"research\"}");

        BackupInventory inventory = await new BackupInventoryPlanner(Paths()).BuildAsync(
            new BackupPlanRequest(
                BackupScope.MetadataOnly,
                SessionId: null,
                Include: [BackupComponent.Configuration],
                Exclude: []),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        Assert.Empty(inventory.Files);

        Assert.Contains(
            inventory.Plan.Components,
            component => component.Component == BackupComponent.Configuration
                && component.Status == BackupComponentStatus.Failed
                && component.Files == 0);
    }

    [Fact]
    public async Task Dynamic_archive_paths_are_normalized_to_unicode_form_c()
    {
        await CreateInventoryDatabaseAsync(seedSql: null);

        const string decomposedName = "cafe\u0301";

        await WriteFileAsync($"spells/{decomposedName}/SPELL.md", "spell");

        BackupInventoryPlanner planner = new(Paths());

        BackupInventory inventory = await planner.BuildAsync(
            new BackupPlanRequest(
                BackupScope.MetadataOnly,
                SessionId: null,
                Include: [BackupComponent.GlobalSpells],
                Exclude: []),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        BackupInventoryFile file = Assert.Single(inventory.Files);

        Assert.Equal("authored/spells/caf\u00e9/SPELL.md", file.ArchivePath);

        Assert.True(file.ArchivePath.IsNormalized());
    }

    [Fact]
    public async Task Explicit_compendium_settings_include_shared_configuration_when_configuration_is_excluded()
    {
        await WriteFileAsync("arcanum.json", "{}");

        await WriteFileAsync("arcanum.preset.json", "{\"presetId\":\"research\"}");

        await WriteFileAsync(
            "arcanum.preset.rollback.json",
            "{\"presetId\":\"research\"}");

        BackupInventoryPlanner planner = new(Paths());

        BackupInventory inventory = await planner.BuildAsync(
            new BackupPlanRequest(
                BackupScope.MetadataOnly,
                SessionId: null,
                Include: [BackupComponent.CompendiumSettings],
                Exclude: [BackupComponent.Configuration]),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        Assert.Equal(
            [
                "configuration/arcanum.json",
                "configuration/arcanum.preset.json",
                "configuration/arcanum.preset.rollback.json",
            ],
            inventory.Files.Select(static file => file.ArchivePath));

        Assert.All(
            inventory.Files,
            static file => Assert.Equal(BackupComponent.CompendiumSettings, file.Component));

        Assert.Contains(
            inventory.Plan.Components,
            component => component.Component == BackupComponent.CompendiumSettings
                && component.Status == BackupComponentStatus.Complete
                && component.Files == 3);

        Assert.Contains(
            inventory.Plan.Components,
            component => component.Component == BackupComponent.Configuration
                && component.Status == BackupComponentStatus.OmittedByPolicy);
    }

    [Fact]
    public async Task Batch_reference_without_uploaded_file_metadata_is_failed_not_complete()
    {
        Guid missingBatchInput = Guid.Parse("22222222-2222-2222-2222-222222222222");

        await CreateInventoryDatabaseAsync(
            $$"""
            INSERT INTO Batches (Id, InputFileId, OutputFileId, ErrorFileId)
            VALUES
                ('33333333-3333-3333-3333-333333333333',
                 '{{missingBatchInput:D}}',
                 NULL,
                 NULL);
            """);

        BackupInventoryPlanner planner = new(Paths());

        BackupInventory inventory = await planner.BuildAsync(
            new BackupPlanRequest(
                BackupScope.MetadataOnly,
                SessionId: null,
                Include: [BackupComponent.BatchArtifacts],
                Exclude: []),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        Assert.Contains(
            inventory.Plan.Components,
            component => component.Component == BackupComponent.BatchArtifacts
                && component.Status == BackupComponentStatus.Failed);

        Assert.Contains(
            Path.Combine(_root, "files", missingBatchInput.ToString("N")),
            inventory.Plan.MissingFiles);
    }

    [Fact]
    public async Task Encrypted_blob_metadata_without_a_key_id_is_failed()
    {
        Guid uploadId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        await CreateInventoryDatabaseAsync(
            $$"""
            INSERT INTO SessionAttachments
                (Id, SessionId, RelativePath, State, EncryptionVersion, EncryptionKeyId)
            VALUES
                ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',
                 'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',
                 'encrypted/attachment.bin',
                 'Bound',
                 1,
                 NULL);

            INSERT INTO UploadedFiles (Id, EncryptionVersion, EncryptionKeyId)
            VALUES ('{{uploadId:D}}', 1, NULL);
            """);

        await WriteFileAsync("attachments/encrypted/attachment.bin", "attachment");

        await WriteFileAsync("files/" + uploadId.ToString("N"), "upload");

        BackupInventoryPlanner planner = new(Paths());

        BackupInventory inventory = await planner.BuildAsync(
            new BackupPlanRequest(
                BackupScope.MetadataOnly,
                SessionId: null,
                Include:
                [
                    BackupComponent.SessionAttachments,
                    BackupComponent.UploadedFiles,
                ],
                Exclude: []),
            _databasePath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        Assert.All(
            new[]
            {
                BackupComponent.SessionAttachments,
                BackupComponent.UploadedFiles,
            },
            component => Assert.Contains(
                inventory.Plan.Components,
                item => item.Component == component
                    && item.Status == BackupComponentStatus.Failed
                    && item.Detail.Contains("key id", StringComparison.OrdinalIgnoreCase)));

        Assert.Empty(inventory.RequiredFileEncryptionKeyIds);
    }

    [Fact]
    public async Task Undefined_components_are_rejected_and_explicit_excludes_win_conflicts()
    {
        BackupInventoryPlanner planner = new(Paths());

        await Assert.ThrowsAsync<ArgumentException>(
            () => planner.BuildAsync(
                new BackupPlanRequest(
                    BackupScope.MetadataOnly,
                    SessionId: null,
                    Include: [(BackupComponent)int.MaxValue],
                    Exclude: []),
                Path.Combine(_root, "operator-supplied.db"),
                databasePassphrase: string.Empty,
                CancellationToken.None));

        BackupInventory excluded = await planner.BuildAsync(
            new BackupPlanRequest(
                BackupScope.MetadataOnly,
                SessionId: null,
                Include: [BackupComponent.Configuration],
                Exclude: [BackupComponent.Configuration]),
            Path.Combine(_root, "operator-supplied.db"),
            databasePassphrase: string.Empty,
            CancellationToken.None);

        Assert.Contains(
            excluded.Plan.Components,
            component => component.Component == BackupComponent.Configuration
                && component.Status == BackupComponentStatus.OmittedByPolicy);

        Assert.False(File.Exists(Path.Combine(_root, "operator-supplied.db")));
    }

    [Fact]
    public async Task Trusted_mcp_inventory_includes_every_rotated_approval_page()
    {
        await WriteFileAsync(
            "trusted-mcp-workspaces.json",
            """{"entries":{"/workspace-a":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}}""");

        await WriteFileAsync(
            "trusted-mcp-workspaces.page-00000001.json",
            """{"entries":{"/workspace-b":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"}}""");

        BackupInventory inventory = await new BackupInventoryPlanner(Paths()).BuildAsync(
            new BackupPlanRequest(
                BackupScope.MetadataOnly,
                SessionId: null,
                Include: [BackupComponent.TrustedMcpWorkspaceMetadata],
                Exclude: []),
            Path.Combine(_root, "missing.db"),
            databasePassphrase: string.Empty,
            CancellationToken.None);

        Assert.Equal(
            [
                "mcp/trusted-workspaces.json",
                "mcp/trusted-workspaces.page-00000001.json",
            ],
            inventory.Files
                .Select(static file => file.ArchivePath)
                .Order(StringComparer.Ordinal));
    }

    private BackupStatePaths Paths() => new(
        _root,
        _root,
        Path.Combine(_root, "audit.jsonl"),
        Path.Combine(_root, "guardrails.jsonl"));

    private Task CreateInventoryDatabaseAsync() =>
        CreateInventoryDatabaseAsync(
            """
            INSERT INTO SessionAttachments
                (Id, SessionId, RelativePath, State, EncryptionVersion, EncryptionKeyId)
            VALUES
                ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',
                 'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',
                 'missing/attachment.bin',
                 'Bound',
                 0,
                 NULL);

            INSERT INTO UploadedFiles (Id, EncryptionVersion, EncryptionKeyId)
            VALUES
                ('11111111-1111-1111-1111-111111111111', 0, NULL),
                ('22222222-2222-2222-2222-222222222222', 0, NULL);

            INSERT INTO Batches (Id, InputFileId, OutputFileId, ErrorFileId)
            VALUES
                ('33333333-3333-3333-3333-333333333333',
                 '22222222-2222-2222-2222-222222222222',
                 NULL,
                 NULL);
            """);

    private async Task CreateInventoryDatabaseAsync(string? seedSql)
    {
        await using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,

                Pooling = false,
            }.ToString());

        await connection.OpenAsync();

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE SessionAttachments (
                Id TEXT PRIMARY KEY,
                SessionId TEXT NULL,
                RelativePath TEXT NOT NULL,
                State TEXT NOT NULL,
                EncryptionVersion INTEGER NOT NULL,
                EncryptionKeyId TEXT NULL
            );

            CREATE TABLE UploadedFiles (
                Id TEXT PRIMARY KEY,
                Purpose TEXT NOT NULL DEFAULT 'assistants',
                EncryptionVersion INTEGER NOT NULL,
                EncryptionKeyId TEXT NULL
            );

            CREATE TABLE Batches (
                Id TEXT PRIMARY KEY,
                InputFileId TEXT NOT NULL,
                OutputFileId TEXT NULL,
                ErrorFileId TEXT NULL
            );
            """);

        if (!string.IsNullOrWhiteSpace(seedSql))
        {
            await ExecuteAsync(connection, seedSql);
        }
    }

    private async Task WriteFileAsync(string relativePath, string content)
    {
        string path = Path.Combine(
            _root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, content);
    }

    /// <summary>
    /// The spelling <c>SessionAttachments</c> actually holds: uppercase, dashed, 36 characters, as the
    /// attachment store renders it.
    /// </summary>
    /// <remarks>
    /// Every seed in this file used to render <c>{identity:D}</c>, which is lowercase, and the planner
    /// bound its <c>$sessionId</c> the same way - so both sides of the session-scoped predicate were the
    /// minority form and the case passed while proving nothing about the spelling the database really
    /// holds. When the attachment family moved to the canonical form the planner's predicate matched no
    /// row, and because the reader simply stopped iterating rather than failing, a session-scoped
    /// archive reported no missing path and no failure while omitting every attachment blob. This suite
    /// was the one instrument that should have caught that, and it did not, because its fixture agreed
    /// with the defect. Seeding what production writes is what makes the case load-bearing.
    /// </remarks>
    private static string Canonical(Guid identity) => identity.ToString("D").ToUpperInvariant();

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }

    private static Dictionary<BackupComponent, BackupInventoryPlanner.ComponentAccumulator> Accumulators() =>
        Enum.GetValues<BackupComponent>().ToDictionary(
            static component => component,
            static component => new BackupInventoryPlanner.ComponentAccumulator(
                component,
                BackupComponentStatus.Complete,
                "Selected."));

    /// <summary>
    /// The planner's own last step, run over files the accumulators have already counted the way the
    /// real enumeration counts them.
    /// </summary>
    private static void Resolve(
        IReadOnlySet<BackupComponent> selected,
        List<BackupInventoryFile> files,
        Dictionary<BackupComponent, BackupInventoryPlanner.ComponentAccumulator> components)
    {
        foreach (BackupInventoryFile file in files)
        {
            components[file.Component].Add(file.Size);
        }

        BackupInventoryPlanner.ResolveArchivePathCollisionsAndAliases(selected, files, components);
    }

    private static BackupInventoryFile InventoryFile(
        BackupComponent component,
        string archivePath) =>
        new(
            component,
            "/source/" + archivePath,
            archivePath,
            Size: 1,
            Sha256: new string('0', 64),
            VolumeId: 1,
            FileId: 1);
}
