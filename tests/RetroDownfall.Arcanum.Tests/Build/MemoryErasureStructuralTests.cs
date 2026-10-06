using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// The selective-erasure structural pins (spec §19.3) that no behavioural test can hold on its own.
/// </summary>
/// <remarks>
/// <para><b>Extraction is the only Saga writer.</b> Saga reports its inference-provider authorship as
/// Known without measuring it, and that is honest only while every memory arrives from a headless
/// extraction. So exactly one statement in <c>src</c> inserts a Saga row, and exactly one production
/// file calls the store's insert.</para>
///
/// <para><b>New erasure log lines are content-free.</b> A log line that names an erased item's content,
/// name or key would copy it into application logs, which an erase never reaches. The scan reads both
/// halves of a line: every template placeholder must be on a short allow-list of known-safe names, and
/// no argument may carry an identifier that could hold content unless a reviewer has read that exact
/// argument. <see cref="ContentFreeLogFiles"/> is closed, and so is the set of files that may log in the
/// erasure family: a file found there that logs must be on that list, or on the small
/// <see cref="ReviewedOlderLoggers"/> list, so a new logging file joins the scan rather than escaping it. The
/// family is every file under <c>src</c> whose path names erasure or whose text names the erasure key,
/// identity, guard or evidence types, plus the folders in <see cref="ErasurePaths"/>. A log call is
/// recognized by what it calls, or by the declared type of its receiver, never by what the receiver is
/// named.</para>
///
/// <para><b>No memory item owns a managed file, and erase is its own verb.</b> A Saga or Lexicon erase
/// is one database transaction, so neither kind may acquire a managed-file executor or reach the
/// managed-file tables. Erase is never offered as a search action or a review action.</para>
///
/// <para><b>No agent tool reaches erase or release.</b> Both are operator-only: release is the unsafe
/// direction, because it lets agents write an erased identity again, so no agent tool may name the
/// erase, release or administration services.</para>
/// </remarks>
public sealed class MemoryErasureStructuralTests
{
    /// <summary>
    /// Every erasure source file whose log lines this scan holds, relative to
    /// <c>src/RetroDownfall.Arcanum.Infrastructure/</c>. Closed: a new erasure source file joins it, and a
    /// file the discovery finds in the erasure family that logs must already be here or on
    /// <see cref="ReviewedOlderLoggers"/>.
    /// </summary>
    /// <remarks>
    /// Existing chokepoint owners such as the Lexicon service are not listed here. They already log names
    /// under the rule that preceded this one, and the content-free rule covers new log lines. They are on
    /// <see cref="ReviewedOlderLoggers"/> instead, so the discovery still knows every one of them.
    /// </remarks>
    internal static readonly string[] ContentFreeLogFiles =
    [
        "Security/MemoryErasureKeyring.cs",
        "Data/MemoryErasureEvidence.cs",
        "Data/MemoryErasureGuard.cs",
        "Data/MemoryErasureKeyWarmup.cs",
        "Data/SagaErasureWriteGate.cs",
        "Data/Covenant/CovenantAgentErasureGate.cs",
        "Memory/MemoryErasureTokens.cs",
        "Data/GrimoireWalCheckpoint.cs",
        "Memory/MemoryErasureScrubber.cs",
        "Memory/MemoryErasureExposure.cs",
        "Memory/MemoryErasureProtocol.cs",
        "Data/SagaRetirementSuppression.cs",
        "Data/MemoryErasureLabels.cs",
        "Memory/SagaMemoryErasureService.cs",
        "Lexicon/LexiconService.Erasure.cs",
        "Data/Covenant/CovenantEntryErasurePlan.cs",
        "Covenant/CovenantEntryErasureService.cs",
        "Memory/MemoryErasureRelease.cs",
        "Data/MemoryErasureFingerprintRelease.cs",
        "Memory/MemoryErasureAdministration.cs",
        "Data/Covenant/ExternalDisclosureStateFold.cs",
        "Data/Covenant/ExternalDisclosureStateReader.cs",
        "Data/Covenant/ExternalDisclosureStateStore.cs",
        "Backup/BackupRestoreErasureEvidence.cs",
        "Backup/BackupRestoreErasureEvidenceApplier.cs",
        "Backup/BackupRestoreService.ErasureEvidence.cs",
        "Backup/BackupRestoreSchemaDrain.cs",
        "Data/CovenantLabeledArtifactGuard.cs",
        "Memory/SagaMemoryReviewService.cs",
        "Covenant/CovenantMemoryReviewService.cs",
    ];

    /// <summary>
    /// The erasure-family files that log under the rule that preceded the content-free one, each by its
    /// repository-relative path with the reason it is not scanned. Closed: a file the discovery finds that
    /// logs and is on neither this list nor <see cref="ContentFreeLogFiles"/> fails the test, and an entry
    /// that no longer logs, or no longer belongs to the family, fails it too, so the list only shrinks.
    /// </summary>
    internal static readonly (string File, string Reason)[] ReviewedOlderLoggers =
    [
        (
            "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.cs",
            "The restore service. Its two warnings carry an operation id, an exception type name and a reversal diagnostic made of paths and counts under the earlier rule, and it reaches the erasure family only through the key provider it hands the erasure-evidence step, which is scanned."),
        (
            "src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs",
            "The older Covenant erasure kernels for managed files and protected artifacts, which log under the rule that preceded the content-free one."),
        (
            "src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs",
            "The data retention service. It logs retention outcomes under the earlier rule and reaches the erasure family only through the evidence type it names."),
        (
            "src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs",
            "Startup and shutdown. It logs lifecycle failures under the earlier rule and reaches the erasure family only by running the key warmup, which is scanned."),
        (
            "src/RetroDownfall.Arcanum.Infrastructure/Hosting/SagaExtractionService.cs",
            "The Saga extraction consumer. It logs session identifiers and counts under the earlier rule and reaches the erasure family only through the guard context it hands the Saga store."),
        (
            "src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs",
            "The Lexicon service. Its failure lines name the entity under the earlier rule; its erasure code is in Lexicon/LexiconService.Erasure.cs, which is scanned."),
    ];

    private const string InfrastructureRoot = "src/RetroDownfall.Arcanum.Infrastructure";

    private const string SagaStore = "src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs";

    private const string SagaExtraction = "src/RetroDownfall.Arcanum.Infrastructure/Hosting/SagaExtractionService.cs";

    private const string EntryErasureService = "src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantEntryErasureService.cs";

    private const string EntryErasureAuthorization = "CovenantSqliteAuthorizationKind.CovenantEntryErasure";

    /// <summary>
    /// An insert or replace into <c>saga_memories</c> as SQLite accepts it: optionally schema-qualified
    /// (<c>main.</c>, <c>temp.</c>, quoted or bracketed), and bare or quoted with double quotes,
    /// brackets, backticks or single quotes.
    /// </summary>
    private static readonly Regex SagaInsert = new(
        @"\b(?:INSERT\s+(?:OR\s+\w+\s+)?|REPLACE\s+)INTO\s+(?:(?:""\w+""|\[\w+\]|`\w+`|'\w+'|\w+)\s*\.\s*)?[""\[`']?saga_memories\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The placeholder names a log template on an erasure path may use. Each names an enum, a count, a flag
    /// or an exception type, never an item, a name, a key or a digest. Closed: a template that needs another
    /// name adds it here in review, beside the argument that fills it.
    /// </summary>
    private static readonly HashSet<string> AllowedPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Store",
        "Count",
        "Kind",
        "KeyState",
        "KeyCreated",
        "WalCheckpointAttempt",
        "Verified",
        "StillPending",
        "FailureType",
        "SqliteErrorCode",
        "SqliteExtendedErrorCode",
        "FingerprintsDiscarded",
        "ReceiptsDiscarded",
    };

    /// <summary>
    /// An identifier that could hold an erased item's content, name, key or digest. A log argument that
    /// mentions one is refused whatever its template says, because an innocuous placeholder name can carry
    /// any argument.
    /// </summary>
    private static readonly Regex ContentBearingIdentifier = new(
        "name|key|content|fact|fingerprint|keyId|digest|token|identity|value|text",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The log arguments a reviewer has read and found content-free although an identifier in them matches
    /// <see cref="ContentBearingIdentifier"/>, each by the exact file and expression. Kept small on purpose.
    /// </summary>
    private static readonly (string File, string Expression, string Reason)[] ReviewedArguments =
    [
        (
            "Memory/MemoryErasureAdministration.cs",
            "counts.Fingerprints",
            "The number of fingerprints a key reset discarded: a count, never a fingerprint."),
        (
            "Memory/MemoryErasureScrubber.cs",
            "failure.GetType().Name",
            "The failing exception's type name, never its message."),
    ];

    /// <summary>
    /// The erasure family's named source paths, relative to <c>src/RetroDownfall.Arcanum.Infrastructure/</c>:
    /// the folder, the file pattern, and whether the folder is read recursively. They are read whatever a
    /// file is called or says; <see cref="IsErasureFamily"/> finds the rest of <c>src</c> by name and text.
    /// </summary>
    private static readonly (string Directory, string Pattern, bool Recursive)[] ErasurePaths =
    [
        ("Memory", "*.cs", true),
        ("Security", "MemoryErasure*.cs", false),
        ("Data", "MemoryErasure*.cs", false),
        ("Covenant", "CovenantEntryErasure*.cs", false),
        ("Data/Covenant", "CovenantEntryErasure*.cs", false),
        ("Data/Covenant", "CovenantAgentErasure*.cs", false),
        ("Backup", "BackupRestoreErasure*.cs", false),
        ("Data", "CovenantLabeledArtifactGuard.cs", false),
        ("Weave", "EmbeddingsResetService.cs", false),
    ];

    /// <summary>
    /// The erase, release and administration services, ports and helpers. The ordinary store ports an
    /// agent tool does use, such as <c>ILexiconService</c> and <c>ISagaMemoryStore</c>, are not here.
    /// </summary>
    private static readonly Regex ForbiddenAgentToolReference = new(
        @"\b(IMemoryErasure\w*|ISagaMemoryErasureService|ILexiconErasureService|ICovenantEntryErasureService|MemoryErasureRelease|MemoryErasureFingerprintRelease|MemoryErasureAdministration|SagaMemoryErasureService|CovenantEntryErasureService)\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex Placeholder = new(
        @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:[,:][^}]*)?\}",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The erasure key, identity, guard and evidence types, by prefix so the provider and exception types
    /// built on them are named too. A file that mentions one is part of the erasure family however it is
    /// named and wherever it lives.
    /// </summary>
    private static readonly Regex ErasureFamilyText = new(
        "MemoryErasure(?:Key|Identity|Guard|Evidence)",
        RegexOptions.CultureInvariant);

    /// <summary>The empty set of declared logger names, for reading an expression that declares none.</summary>
    private static readonly HashSet<string> NoLoggers = new(StringComparer.Ordinal);

    [Fact]
    public void Extraction_is_the_only_saga_writer()
    {
        (string Path, int Matches)[] inserts =
        [
            .. Sources("*.cs")
                .Concat(Sources("*.sql"))
                .Select(source => (source.Path, SagaInsert.Matches(source.Text).Count))
                .Where(static source => source.Count > 0)
                .OrderBy(static source => source.Path, StringComparer.Ordinal),
        ];

        Assert.Equal([(SagaStore, 1)], inserts);

        (string Path, int Calls)[] callers =
        [
            .. Sources("*.cs")
                .Where(static source => NamesSagaStore(source.Text))
                .Select(static source => (source.Path, InsertAsyncCalls(source.Text)))
                .Where(static source => source.Item2 > 0)
                .OrderBy(static source => source.Path, StringComparer.Ordinal),
        ];

        Assert.Equal([(SagaExtraction, 2)], callers);
    }

    /// <summary>
    /// The writer pin is only as good as its pattern, so each spelling SQLite accepts is shown to be
    /// caught before the pin's silence is trusted.
    /// </summary>
    [Theory]
    [InlineData("INSERT INTO saga_memories (Id) VALUES ($id)")]
    [InlineData("insert or ignore into \"saga_memories\" (Id) VALUES ($id)")]
    [InlineData("INSERT OR REPLACE INTO saga_memories (Id) VALUES ($id)")]
    [InlineData("REPLACE INTO saga_memories (Id) VALUES ($id)")]
    [InlineData("INSERT INTO main.saga_memories (Id) VALUES ($id)")]
    [InlineData("INSERT INTO temp.saga_memories (Id) VALUES ($id)")]
    [InlineData("INSERT INTO \"main\".\"saga_memories\" (Id) VALUES ($id)")]
    [InlineData("INSERT INTO [saga_memories] (Id) VALUES ($id)")]
    [InlineData("INSERT INTO [main].[saga_memories] (Id) VALUES ($id)")]
    [InlineData("INSERT INTO `saga_memories` (Id) VALUES ($id)")]
    [InlineData("INSERT INTO `main` . `saga_memories` (Id) VALUES ($id)")]
    [InlineData("INSERT INTO 'saga_memories' (Id) VALUES ($id)")]
    [InlineData("INSERT INTO 'main'.'saga_memories' (Id) VALUES ($id)")]
    public void The_saga_writer_pin_recognizes_every_spelling_of_an_insert(string statement)
    {
        Assert.Single(SagaInsert.Matches(statement));
    }

    [Theory]
    [InlineData("INSERT INTO saga_memories_archive (Id) VALUES ($id)")]
    [InlineData("INSERT INTO saga_memory_embeddings (MemoryId) VALUES ($id)")]
    [InlineData("INSERT INTO main.saga_memories_v2 (Id) VALUES ($id)")]
    [InlineData("UPDATE saga_memories SET Content = $content")]
    public void The_saga_writer_pin_ignores_other_tables_and_statements(string statement)
    {
        Assert.DoesNotMatch(SagaInsert, statement);
    }

    /// <summary>
    /// A caller holding the concrete store is a caller too, so the scan reads every file that names
    /// either the port or the store.
    /// </summary>
    [Fact]
    public void The_saga_caller_scan_counts_the_concrete_store_and_the_port()
    {
        const string concrete = """
            internal sealed class Fixture(SagaMemoryStore store)
            {
                internal Task Write(string id) => store.InsertAsync(id, "x", default, null, null, null, [], default);
            }
            """;

        const string port = """
            internal sealed class Fixture(ISagaMemoryStore memories)
            {
                internal async Task Write(string id)
                {
                    _ = await memories.InsertAsync(id, "x", default, null, null, null, [], default);
                    _ = await this.memories.InsertAsync(id, "y", default, null, null, null, [], default);
                }
            }
            """;

        const string unrelated = """
            internal sealed class Fixture(ITapestryStore tapestry)
            {
                internal Task Write() => tapestry.InsertAsync(default);
            }
            """;

        Assert.True(NamesSagaStore(concrete));

        Assert.Equal(1, InsertAsyncCalls(concrete));

        Assert.True(NamesSagaStore(port));

        Assert.Equal(2, InsertAsyncCalls(port));

        Assert.False(NamesSagaStore(unrelated));
    }

    /// <summary>
    /// A Saga memory and a Lexicon entry are database rows only: their erase is one transaction, and no
    /// managed workspace file or erasure work item belongs to either (spec §2, §19.3).
    /// </summary>
    [Fact]
    public void No_memory_item_owns_a_managed_file()
    {
        Assert.Equal(
            CovenantArtifactPurgeExecutor.DatabaseTransaction,
            CovenantSensitiveArtifactPurgePolicy.Resolve(SensitiveArtifactKind.Saga).Value.Executor);

        Assert.Equal(
            CovenantArtifactPurgeExecutor.DatabaseTransaction,
            CovenantSensitiveArtifactPurgePolicy.Resolve(SensitiveArtifactKind.Lexicon).Value.Executor);

        Assert.Equal(
            [SensitiveArtifactKind.ManagedWorkspaceFile],
            CovenantSensitiveArtifactPurgePolicy.All
                .Where(static rule => rule.Executor == CovenantArtifactPurgeExecutor.ManagedFileKernel)
                .Select(static rule => rule.Kind));

        string[] managedFileTables = ["managed_file_write_intents", "local_erasure_work_items"];

        foreach (SensitiveArtifactKind kind in (SensitiveArtifactKind[])[SensitiveArtifactKind.Saga, SensitiveArtifactKind.Lexicon])
        {
            CovenantArtifactPurgePlan plan = CovenantArtifactPurgePlans.Resolve(kind);

            string[] tables =
            [
                .. plan.Projections.Select(static projection => projection.Table),
                .. plan.Artifact is { } artifact ? [artifact.Table] : Array.Empty<string>(),
            ];

            Assert.NotEmpty(tables);

            Assert.Empty(tables.Intersect(managedFileTables, StringComparer.Ordinal));
        }

        Assert.Empty(CovenantCanonicalContentTables.InDeletionOrder.Intersect(managedFileTables, StringComparer.Ordinal));
    }

    /// <summary>Erase is its own verb: never a search action and never a review action (spec §17.2).</summary>
    [Fact]
    public void Erase_is_not_a_search_or_review_action()
    {
        Assert.Equal(["ShowSagaMemory", "ShowLexiconEntry"], Enum.GetNames<MemorySearchActionKind>());

        Assert.Equal(["Confirm", "Correct", "Retire", "Pin", "Unpin"], Enum.GetNames<MemoryReviewAction>());
    }

    /// <summary>
    /// Erase and release are operator-only, so no agent tool partial names the services that do them
    /// (spec §19.3).
    /// </summary>
    [Fact]
    public void No_agent_tool_references_the_erase_or_release_services()
    {
        string[] files =
        [
            .. Sources("ArcanumInternalToolServer*.cs")
                .Select(static source => source.Path)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Contains("src/RetroDownfall.Arcanum.Infrastructure/Mcp/ArcanumInternalToolServer.cs", files);

        Assert.Contains("src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.LexiconTools.cs", files);

        // The fifteen partials present when this pin was written; a new one joins the scan by its name.
        Assert.True(files.Length >= 15, $"Only {files.Length} agent tool partials were found.");

        Assert.Empty(
            Sources("ArcanumInternalToolServer*.cs")
                .Where(static source => ForbiddenAgentToolReference.IsMatch(source.Text))
                .Select(static source => source.Path));
    }

    /// <summary>The agent-tool pin is only as good as its pattern, so each forbidden name is shown to be caught.</summary>
    [Theory]
    [InlineData("IMemoryErasureRelease release,")]
    [InlineData("IMemoryErasureKeyProvider keys,")]
    [InlineData("IMemoryErasureAdministration administration,")]
    [InlineData("ISagaMemoryErasureService erase,")]
    [InlineData("ILexiconErasureService erase,")]
    [InlineData("ICovenantEntryErasureService erase,")]
    [InlineData("MemoryErasureRelease release = new(db, keys, logger);")]
    [InlineData("_ = await MemoryErasureFingerprintRelease.DeleteCandidatesAsync(connection, transaction, key, candidates, ct);")]
    [InlineData("MemoryErasureAdministration administration,")]
    [InlineData("SagaMemoryErasureService erase,")]
    [InlineData("CovenantEntryErasureService erase,")]
    public void The_agent_tool_pattern_recognizes_each_service_name(string line) =>
        Assert.Matches(ForbiddenAgentToolReference, line);

    [Fact]
    public void The_agent_tool_pattern_ignores_the_ordinary_store_ports() =>
        Assert.DoesNotMatch(ForbiddenAgentToolReference, "ILexiconService lexicon, ISagaMemoryStore saga");

    /// <summary>
    /// A restore reads the erasure key and never creates one: its destination read probes the keychain,
    /// and its staged evidence step and post-commit proof use only the key that read latched. So no file
    /// under the restore's own folder may reach the creator.
    /// </summary>
    [Fact]
    public void Restore_never_names_the_erasure_key_creator()
    {
        (string Path, string Text)[] backup =
        [
            .. Sources("*.cs").Where(static source =>
                source.Path.StartsWith("src/RetroDownfall.Arcanum.Infrastructure/Backup/", StringComparison.Ordinal)),
        ];

        Assert.Contains(backup, static source => source.Path.EndsWith("/BackupRestoreErasureEvidenceApplier.cs", StringComparison.Ordinal));

        Assert.Empty(
            backup
                .Where(static source => Regex.IsMatch(source.Text, @"\b(?:IMemoryErasureKeyCreator|OpenOrCreate|CreateForReset)\b"))
                .Select(static source => source.Path));
    }

    /// <summary>
    /// Authorization kind 12 opens every canonical delete guard an entry erasure needs, so exactly one
    /// production file may grant it: the entry-erasure service, which grants it to its own erase
    /// transaction. The plan it runs deletes under whatever its caller granted.
    /// </summary>
    [Fact]
    public void Only_the_entry_erasure_service_names_its_authorization_kind()
    {
        string[] declaring =
        [
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSqliteAuthorizationKind.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSqliteConnectionInitializer.cs",
        ];

        string[] naming =
        [
            .. Sources("*.cs")
                .Where(source => !declaring.Contains(source.Path, StringComparer.Ordinal))
                .Where(static source => source.Text.Contains("CovenantEntryErasure", StringComparison.Ordinal))
                .Where(static source => MemberAccesses(source.Text, EntryErasureAuthorization) > 0)
                .Select(static source => source.Path)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal([EntryErasureService], naming);
    }

    /// <summary>
    /// A disposition completed with a cancelled request token claims the lease's one disposition and
    /// then throws, which leaves the entry's scope closed until the host restarts. Every completion in
    /// the entry-erasure service therefore passes <see cref="CancellationToken.None"/>.
    /// </summary>
    [Fact]
    public void The_entry_erasure_service_completes_every_closure_with_CancellationToken_None()
    {
        string path = Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), EntryErasureService);

        InvocationExpressionSyntax[] completions =
        [
            .. CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Preview))
                .GetCompilationUnitRoot()
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(static invocation => InvokedName(invocation) == "CompleteAsync"),
        ];

        Assert.NotEmpty(completions);

        Assert.All(
            completions,
            static invocation => Assert.Equal(
                "CancellationToken.None",
                invocation.ArgumentList.Arguments.Last().Expression.ToString()));
    }

    [Fact]
    public void New_erasure_log_templates_are_content_free()
    {
        string root = Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), InfrastructureRoot);

        Assert.Equal(ContentFreeLogFiles.Length, ContentFreeLogFiles.Distinct(StringComparer.Ordinal).Count());

        List<string> violations = [];

        foreach (string file in ContentFreeLogFiles)
        {
            string path = Path.Combine(root, file);

            Assert.True(File.Exists(path), $"{InfrastructureRoot}/{file} is listed but does not exist.");

            violations.AddRange(
                LogTemplateViolations(File.ReadAllText(path), file).Select(violation => $"{file}: {violation}"));
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// A reviewed argument exception names a file and an expression that still exist, so a stale one is
    /// removed rather than left to excuse a later change.
    /// </summary>
    [Fact]
    public void Every_reviewed_log_argument_is_still_logged_by_its_file()
    {
        string root = Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), InfrastructureRoot);

        Assert.All(
            ReviewedArguments,
            reviewed =>
            {
                Assert.Contains(reviewed.File, ContentFreeLogFiles);

                Assert.False(string.IsNullOrWhiteSpace(reviewed.Reason));

                Assert.Contains(
                    reviewed.Expression,
                    LoggedArgumentExpressions(File.ReadAllText(Path.Combine(root, reviewed.File))));
            });
    }

    /// <summary>
    /// Every file in the erasure family that logs is one the scan reads, or one a reviewer has named as an
    /// older logger. The lists are closed by hand, so this is what makes a new logging file join them instead
    /// of passing unread.
    /// </summary>
    [Fact]
    public void Every_file_on_an_erasure_path_that_logs_is_in_the_scan()
    {
        (string Path, string Text)[] discovered = DiscoverErasureSources();

        // The discovery is only as good as its rules, so it is shown to reach each kind of file it names:
        // the named folders, a path that says erasure, and a text that names the erasure types.
        string[] reached =
        [
            "Memory/MemoryErasureRelease.cs",
            "Security/MemoryErasureKeyring.cs",
            "Data/MemoryErasureKeyWarmup.cs",
            "Covenant/CovenantEntryErasureService.cs",
            "Data/Covenant/CovenantEntryErasurePlan.cs",
            "Data/Covenant/CovenantAgentErasureGate.cs",
            "Backup/BackupRestoreErasureEvidenceApplier.cs",
            "Data/CovenantLabeledArtifactGuard.cs",
            "Weave/EmbeddingsResetService.cs",
            "Data/CovenantErasureCoordinator.cs",
            "Lexicon/LexiconService.cs",
        ];

        Assert.All(reached, path => Assert.Contains(discovered, source => source.Path == $"{InfrastructureRoot}/{path}"));

        Assert.Empty(UnscannedLoggingFiles(discovered, ScannedLogFiles()));
    }

    /// <summary>
    /// The older loggers are a closed list of files that exist, still log, are still found by the discovery,
    /// and are not also scanned, each with a reason. An entry that stops being true is removed, so the list
    /// cannot excuse a file that has since changed.
    /// </summary>
    [Fact]
    public void The_reviewed_older_loggers_are_closed_and_still_current()
    {
        string repositoryRoot = NativeSqlCipherTestPaths.RepositoryRoot();

        string[] files = [.. ReviewedOlderLoggers.Select(static reviewed => reviewed.File)];

        Assert.Equal(files.Length, files.Distinct(StringComparer.Ordinal).Count());

        (string Path, string Text)[] discovered = DiscoverErasureSources();

        Assert.All(
            ReviewedOlderLoggers,
            reviewed =>
            {
                Assert.False(string.IsNullOrWhiteSpace(reviewed.Reason), $"{reviewed.File} has no reason.");

                Assert.True(File.Exists(Path.Combine(repositoryRoot, reviewed.File)), $"{reviewed.File} is listed but does not exist.");

                Assert.True(
                    discovered.Any(source => source.Path == reviewed.File),
                    $"{reviewed.File} is no longer found by the erasure discovery, so it does not need to be listed.");

                Assert.True(
                    LogsAnything(File.ReadAllText(Path.Combine(repositoryRoot, reviewed.File))),
                    $"{reviewed.File} no longer logs, so it does not need to be listed.");

                Assert.DoesNotContain(
                    ContentFreeLogFiles,
                    file => $"{InfrastructureRoot}/{file}" == reviewed.File);
            });
    }

    /// <summary>
    /// A file belongs to the erasure family when its path says erasure or its text names the erasure key,
    /// identity, guard or evidence types, wherever under <c>src</c> it lives.
    /// </summary>
    [Theory]
    [InlineData("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaErasureProbe.cs", "internal sealed class Probe;", true)]
    [InlineData("src/RetroDownfall.Arcanum.Api/Erasure/Probe.cs", "internal sealed class Probe;", true)]
    [InlineData("src/RetroDownfall.Arcanum.Api/Tower/AuditProbe.cs", "using Secrets; MemoryErasureKeyState state;", true)]
    [InlineData("src/RetroDownfall.Arcanum.Api/Tower/AuditProbe.cs", "IMemoryErasureKeyProvider keys;", true)]
    [InlineData("src/RetroDownfall.Arcanum.Api/Tower/AuditProbe.cs", "MemoryErasureIdentity identity;", true)]
    [InlineData("src/RetroDownfall.Arcanum.Api/Tower/AuditProbe.cs", "throw new MemoryErasureGuardException(error);", true)]
    [InlineData("src/RetroDownfall.Arcanum.Api/Tower/AuditProbe.cs", "MemoryErasureEvidence.CountAsync(connection);", true)]
    [InlineData("src/RetroDownfall.Arcanum.Api/Tower/AuditProbe.cs", "internal sealed class Probe;", false)]
    [InlineData("src/RetroDownfall.Arcanum.Api/Tower/AuditProbe.cs", "MemoryErasureRelease release;", false)]
    public void The_erasure_family_is_found_by_path_and_by_the_types_a_file_names(string path, string text, bool expected) =>
        Assert.Equal(expected, IsErasureFamily(path, text));

    /// <summary>
    /// A logging file the scan does not read is found however its logger is named: by the declared type of
    /// the receiver, whether that is a field, a property, a parameter, a primary-constructor parameter or a
    /// local, and whether it is called plainly or through a null-conditional.
    /// </summary>
    [Fact]
    public void The_discovery_finds_a_logging_file_whatever_its_logger_is_named()
    {
        (string Path, string Text)[] sources =
        [
            (
                "Data/SagaErasureProbe.cs",
                """
                internal sealed class Probe(Serilog.ILogger audit)
                {
                    internal void Erase(string name) => audit.Information("Probe {Detail}.", name);
                }
                """),
            (
                "Data/Field.cs",
                """
                internal sealed class Probe
                {
                    private readonly Serilog.ILogger trail = Serilog.Log.Logger;

                    internal void Erase(string name) => trail.Debug("Probe {Detail}.", name);
                }
                """),
            (
                "Data/Property.cs",
                """
                internal sealed class Probe
                {
                    private Serilog.ILogger? Ledger { get; init; }

                    internal void Erase(string name) => Ledger?.Warning("Probe {Detail}.", name);
                }
                """),
            (
                "Data/Local.cs",
                """
                internal sealed class Probe
                {
                    internal void Erase(string name)
                    {
                        Serilog.ILogger journal = Serilog.Log.Logger;

                        journal.Error("Probe {Detail}.", name);
                    }
                }
                """),
            (
                "Data/Var.cs",
                """
                internal sealed class Probe
                {
                    internal void Erase(string name)
                    {
                        var scoped = Serilog.Log.ForContext<Probe>();

                        scoped.Warning("Probe {Detail}.", name);
                    }
                }
                """),
            (
                "Data/Enrich.cs",
                """
                internal sealed class Probe(Serilog.ILogger audit)
                {
                    internal void Erase(string name) => audit.ForContext("Subject", name);
                }
                """),
        ];

        Assert.Equal(
            [.. sources.Select(static source => source.Path).Order(StringComparer.Ordinal)],
            UnscannedLoggingFiles(sources, []).Order(StringComparer.Ordinal));

        // A receiver is a logger by its type and never by its name: the same names on another type are
        // not logging.
        (string Path, string Text)[] quiet =
        [
            (
                "Data/NotALogger.cs",
                """
                internal sealed class Probe(Recorder audit, Recorder logger)
                {
                    internal void Erase(string name)
                    {
                        audit.Information("Probe {Detail}.", name);

                        logger.Warning("Probe {Detail}.", name);
                    }
                }
                """),
        ];

        Assert.Empty(UnscannedLoggingFiles(quiet, []));
    }

    [Fact]
    public void The_discovery_flags_a_logging_file_the_scan_does_not_read()
    {
        const string logging = """
            internal sealed class Fixture(Microsoft.Extensions.Logging.ILogger logger)
            {
                internal void Erase(int store) => logger.LogInformation("Erased in {Store}.", store);
            }
            """;

        const string attribute = """
            internal static partial class Fixture
            {
                [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Erased in {Store}.")]
                internal static partial void Erased(ILogger logger, int store);
            }
            """;

        const string silent = """
            internal sealed class Fixture
            {
                internal int Erase(int store) => store + 1;
            }
            """;

        (string Path, string Text)[] sources =
        [
            ("Memory/NewEraser.cs", logging),
            ("Memory/NewAttributeEraser.cs", attribute),
            ("Memory/Quiet.cs", silent),
            ("Memory/Listed.cs", logging),
        ];

        Assert.Equal(
            ["Memory/NewAttributeEraser.cs", "Memory/NewEraser.cs"],
            UnscannedLoggingFiles(sources, ["Memory/Listed.cs"]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_log_template_scan_flags_a_named_placeholder()
    {
        const string flagged = """
            internal sealed class Fixture(Microsoft.Extensions.Logging.ILogger logger)
            {
                internal void Erase(int store) => logger.LogWarning("Erased {Name}.", store);
            }
            """;

        Assert.Single(LogTemplateViolations(flagged));

        const string attribute = """
            internal static partial class Fixture
            {
                [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Erased {normalizedKey:l} at {Store}.")]
                internal static partial void Erased(ILogger logger, string normalizedKey, int store);

                [LoggerMessage(2, LogLevel.Information, "Released {Facts}.")]
                internal static partial void Released(ILogger logger, string facts);
            }
            """;

        // Each method is refused twice, once for the placeholder it names and once for the parameter that
        // fills it, because either half alone could carry the content.
        Assert.Equal(4, LogTemplateViolations(attribute).Count);

        const string clean = """
            internal sealed class Fixture(Microsoft.Extensions.Logging.ILogger logger)
            {
                internal void Erase(int store) => logger.LogInformation("Erased one item in store {Store}; {Count} rows.", store, 1);

                internal string Describe(string name) => string.Format("Erased {Name}.", name);
            }
            """;

        Assert.Empty(LogTemplateViolations(clean));
    }

    /// <summary>
    /// The template is not the only half of a log line that can carry content: an innocuous placeholder name
    /// can be filled with anything. A placeholder off the allow-list is refused, and so is any argument whose
    /// identifier could hold content, whatever it is logged under.
    /// </summary>
    [Theory]
    [InlineData("logger.LogInformation(\"Probe {Detail}.\", Convert.ToHexString(keyId));", 2)]
    [InlineData("logger.LogInformation(\"Probe {Store}.\", Convert.ToHexString(keyId));", 1)]
    [InlineData("logger.LogInformation(\"Probe {Store}.\", fingerprint);", 1)]
    [InlineData("logger.LogInformation(\"Probe {Count}.\", item.Content.Length);", 1)]
    [InlineData("logger.LogInformation(\"Probe {Store}.\", subject.NormalizedName);", 1)]
    [InlineData("logger.LogInformation(\"Probe {Store}.\", requestDigest);", 1)]
    [InlineData("logger.LogInformation(\"Probe {Store}.\", preflightToken);", 1)]
    [InlineData("logger.LogInformation(\"Probe {Store}.\", identity.Value);", 1)]
    [InlineData("logger.LogInformation(\"Probe {Store}.\", facts.Text);", 1)]
    [InlineData("logger.LogInformation(\"Probe {Detail}.\", store);", 1)]
    [InlineData("logger.LogInformation(\"Probe {Store}.\", store);", 0)]
    [InlineData("logger.LogInformation(\"Probe {Store}: {Count}.\", store, rows.Count);", 0)]
    [InlineData("logger.LogInformation(\"Probe {Store}: {FailureType}.\", store, failure.GetType().Name);", 1)]
    public void The_log_template_scan_refuses_an_unreviewed_placeholder_or_a_content_bearing_argument(string call, int expected)
    {
        string source = $$"""
            internal sealed class Fixture(Microsoft.Extensions.Logging.ILogger logger)
            {
                internal void Erase(int store)
                {
                    {{call}}
                }
            }
            """;

        Assert.Equal(expected, LogTemplateViolations(source).Count);
    }

    /// <summary>
    /// A reviewed argument excuses exactly the expression a reviewer read in exactly the file it was read
    /// in, and nothing else that happens to match.
    /// </summary>
    [Fact]
    public void A_reviewed_argument_excuses_only_its_own_file_and_expression()
    {
        const string source = """
            internal sealed class Fixture(Microsoft.Extensions.Logging.ILogger logger)
            {
                internal void Reset(Counts counts, Exception failure)
                {
                    logger.LogInformation("Discarded {FingerprintsDiscarded}.", counts.Fingerprints);

                    logger.LogInformation("Discarded {FingerprintsDiscarded}.", counts.FingerprintList);
                }
            }
            """;

        Assert.Equal(2, LogTemplateViolations(source).Count);

        Assert.Equal(2, LogTemplateViolations(source, "Memory/MemoryErasureRelease.cs").Count);

        Assert.Single(LogTemplateViolations(source, "Memory/MemoryErasureAdministration.cs"));
    }

    /// <summary>
    /// Every logging call shape the listed files use, or could, is scanned: Serilog's static and
    /// instance API as well as <c>ILogger</c>, message definitions, scopes, and interpolated
    /// templates, which carry whatever they interpolate into the log whatever the placeholder is named.
    /// </summary>
    [Theory]
    [InlineData("Log.Warning(\"Erased {Name}.\", store);", 1)]
    [InlineData("Serilog.Log.Error(\"Released {Content}.\", store);", 1)]
    [InlineData("global::Serilog.Log.Information(\"Erased {Key}.\", store);", 1)]
    [InlineData("Log.ForContext<Fixture>().Debug(\"Erased {Facts}.\", store);", 1)]
    [InlineData("_log.Verbose(\"Erased {NormalizedKey}.\", store);", 1)]
    [InlineData("Log.Write(LogEventLevel.Warning, \"Erased {Fact}.\", store);", 1)]
    [InlineData("Log.Fatal(\"Erased {name}.\", store);", 1)]
    [InlineData("logger.LogWarning(\"Erased {Name}.\", store);", 1)]
    [InlineData("logger.Log(LogLevel.Warning, \"Erased {Name}.\", store);", 1)]
    [InlineData("_ = logger.BeginScope(\"Erasing {Name}.\", store);", 1)]
    [InlineData("_ = LoggerMessage.Define<string>(LogLevel.Information, new EventId(1), \"Erased {Key}.\");", 1)]
    [InlineData("Log.Information($\"Erased {name}.\");", 1)]
    [InlineData("logger.LogInformation($\"Erased {name}.\");", 1)]
    [InlineData("audit.Information(\"Erased {Name}.\", store);", 1)]
    [InlineData("audit?.Warning(\"Erased {Name}.\", store);", 1)]
    [InlineData("_audit.Error(\"Erased {Name}.\", store);", 1)]
    [InlineData("this._audit.Error(\"Erased {Name}.\", store);", 1)]
    [InlineData("audit.ForContext(\"Subject\", name);", 1)]
    [InlineData("audit.ForContext<Fixture>().Information(\"Erased {Name}.\", store);", 1)]
    [InlineData("logger?.LogWarning(\"Erased {Name}.\", store);", 1)]
    [InlineData("var scoped = Log.ForContext<Fixture>(); scoped.Warning(\"Erased {Name}.\", store);", 1)]
    [InlineData("recorder.Information(\"Erased {Name}.\", store);", 0)]
    [InlineData("audit.Information(\"The erasure key is {KeyState}.\", state);", 0)]
    [InlineData("Log.Warning(\"The erasure key is {KeyState}.\", state);", 0)]
    [InlineData("logger.LogInformation(\"Checkpoint attempt: {WalCheckpointAttempt}.\", attempt);", 0)]
    [InlineData("_ = string.Format(\"Erased {Name}.\", name);", 0)]
    public void The_log_template_scan_reads_every_logging_call_shape(string call, int expected)
    {
        string source = $$"""
            internal sealed class Fixture(
                Microsoft.Extensions.Logging.ILogger logger,
                Serilog.ILogger _log,
                Serilog.ILogger audit,
                Recorder recorder)
            {
                private readonly Serilog.ILogger _audit = Serilog.Log.Logger;

                internal void Erase(string name)
                {
                    {{call}}
                }
            }
            """;

        Assert.Equal(expected, LogTemplateViolations(source).Count);
    }

    /// <summary>
    /// Everything in a log line on an erasure path that could copy content into a log: a placeholder off the
    /// allow-list in a template passed to a <c>Log…</c> invocation or declared on a <c>[LoggerMessage]</c>
    /// attribute, an interpolated string, and an argument or declared parameter with an identifier that could
    /// hold content.
    /// </summary>
    /// <param name="source">The file's text.</param>
    /// <param name="file">
    /// The file's path relative to the Infrastructure root, which is what a reviewed argument is keyed by;
    /// null for a fixture, which has none.
    /// </param>
    private static List<string> LogTemplateViolations(string source, string? file = null)
    {
        CompilationUnitSyntax root = ParseUnit(source);

        List<string> violations = [];

        foreach (InvocationExpressionSyntax invocation in LogCalls(root))
        {
            foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
            {
                ScanTemplate(argument, violations);

                ScanArgumentIdentifiers(argument.Expression, file, violations);
            }
        }

        foreach (AttributeSyntax attribute in LoggerMessageAttributes(root).Where(static attribute => attribute.ArgumentList is not null))
        {
            ScanTemplate(attribute.ArgumentList!, violations);

            if (attribute.Parent?.Parent is MethodDeclarationSyntax method)
            {
                foreach (ParameterSyntax parameter in method.ParameterList.Parameters
                    .Where(static parameter => !IsLoggerType(parameter.Type) && parameter.Type?.ToString() is not "Exception"))
                {
                    string name = parameter.Identifier.ValueText;

                    if (ContentBearingIdentifier.IsMatch(name) && !IsReviewed(file, name))
                    {
                        violations.Add($"the parameter {name} of the log message {method.Identifier.ValueText}");
                    }
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// The placeholders in every string literal under a node that are not on the allow-list, and every
    /// interpolated string under it, which carries whatever it interpolates whatever the placeholder is named.
    /// </summary>
    private static void ScanTemplate(SyntaxNode node, List<string> violations)
    {
        foreach (LiteralExpressionSyntax literal in node.DescendantNodesAndSelf()
            .OfType<LiteralExpressionSyntax>()
            .Where(static literal => literal.IsKind(SyntaxKind.StringLiteralExpression)))
        {
            foreach (Match match in Placeholder.Matches(literal.Token.ValueText))
            {
                if (!AllowedPlaceholders.Contains(match.Groups["name"].Value))
                {
                    violations.Add($"{match.Value} in \"{literal.Token.ValueText}\" is not an allowed placeholder");
                }
            }
        }

        foreach (InterpolatedStringExpressionSyntax interpolated in node.DescendantNodesAndSelf()
            .OfType<InterpolatedStringExpressionSyntax>())
        {
            violations.Add($"an interpolated string in a log call: {interpolated}");
        }
    }

    /// <summary>
    /// An argument that mentions an identifier which could hold content, unless a reviewer has read that
    /// exact expression in that exact file. An interpolated string is refused as a whole by
    /// <see cref="ScanTemplate"/>, so its identifiers are not counted twice.
    /// </summary>
    private static void ScanArgumentIdentifiers(ExpressionSyntax expression, string? file, List<string> violations)
    {
        if (expression.DescendantNodesAndSelf().OfType<InterpolatedStringExpressionSyntax>().Any())
        {
            return;
        }

        string[] matching =
        [
            .. expression.DescendantTokens()
                .Where(static token => token.IsKind(SyntaxKind.IdentifierToken))
                .Select(static token => token.ValueText)
                .Where(static identifier => ContentBearingIdentifier.IsMatch(identifier))
                .Distinct(StringComparer.Ordinal),
        ];

        if (matching.Length > 0 && !IsReviewed(file, Compact(expression)))
        {
            violations.Add($"the argument {Compact(expression)} names {string.Join(", ", matching)}");
        }
    }

    private static bool IsReviewed(string? file, string expression) =>
        file is not null
        && ReviewedArguments.Any(reviewed =>
            string.Equals(reviewed.File, file, StringComparison.Ordinal)
            && string.Equals(reviewed.Expression, expression, StringComparison.Ordinal));

    /// <summary>An expression's text with every run of whitespace removed, so a reviewed one survives a reflow.</summary>
    private static string Compact(SyntaxNode node) =>
        string.Concat(node.ToString().Where(static character => !char.IsWhiteSpace(character)));

    /// <summary>The compacted text of every non-template argument of every log call in a file.</summary>
    private static List<string> LoggedArgumentExpressions(string source) =>
    [
        .. LogCalls(ParseUnit(source))
            .SelectMany(static invocation => invocation.ArgumentList.Arguments)
            .Select(static argument => Compact(argument.Expression)),
    ];

    private static IEnumerable<AttributeSyntax> LoggerMessageAttributes(CompilationUnitSyntax root) =>
        root.DescendantNodes()
            .OfType<AttributeSyntax>()
            .Where(static attribute => attribute.Name.ToString() is "LoggerMessage" or "LoggerMessageAttribute"
                || attribute.Name.ToString().EndsWith(".LoggerMessage", StringComparison.Ordinal)
                || attribute.Name.ToString().EndsWith(".LoggerMessageAttribute", StringComparison.Ordinal));

    /// <summary>Whether a file writes any log line or declares any log message.</summary>
    private static bool LogsAnything(string source)
    {
        CompilationUnitSyntax root = ParseUnit(source);

        return LogCalls(root).Count > 0 || LoggerMessageAttributes(root).Any();
    }

    /// <summary>
    /// The files in the erasure family that log, and are not among the files the scan reads or a reviewer
    /// has named. Each is a file whose log lines nothing has checked.
    /// </summary>
    private static List<string> UnscannedLoggingFiles(
        IEnumerable<(string Path, string Text)> sources,
        IEnumerable<string> scanned)
    {
        HashSet<string> read = new(scanned, StringComparer.Ordinal);

        return
        [
            .. sources
                .Where(source => !read.Contains(source.Path) && LogsAnything(source.Text))
                .Select(static source => source.Path),
        ];
    }

    /// <summary>
    /// Every file the scan reads or a reviewer has named, by repository-relative path: the content-free
    /// files, which are listed relative to the Infrastructure root, and the reviewed older loggers.
    /// </summary>
    private static IEnumerable<string> ScannedLogFiles() =>
    [
        .. ContentFreeLogFiles.Select(static file => $"{InfrastructureRoot}/{file}"),
        .. ReviewedOlderLoggers.Select(static reviewed => reviewed.File),
    ];

    /// <summary>
    /// Whether a file under <c>src</c> belongs to the erasure family by what it is called or says: its path
    /// names erasure, or its text names the erasure key, identity, guard or evidence types.
    /// </summary>
    /// <param name="path">The file's repository-relative path.</param>
    /// <param name="text">The file's text.</param>
    private static bool IsErasureFamily(string path, string text) =>
        path.Contains("Erasure", StringComparison.OrdinalIgnoreCase) || ErasureFamilyText.IsMatch(text);

    /// <summary>
    /// Every erasure-family source file: the folders on <see cref="ErasurePaths"/> and every file anywhere
    /// under <c>src</c> that <see cref="IsErasureFamily"/> finds, each with its repository-relative path
    /// and its text.
    /// </summary>
    private static (string Path, string Text)[] DiscoverErasureSources()
    {
        string root = Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), InfrastructureRoot);

        List<(string Path, string Text)> found = [];

        foreach ((string directory, string pattern, bool recursive) in ErasurePaths)
        {
            string folder = Path.Combine(root, directory);

            Assert.True(Directory.Exists(folder), $"{InfrastructureRoot}/{directory} is named as an erasure path but does not exist.");

            foreach (string file in Directory.EnumerateFiles(
                folder,
                pattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            {
                found.Add(($"{InfrastructureRoot}/{Path.GetRelativePath(root, file).Replace('\\', '/')}", File.ReadAllText(file)));
            }
        }

        found.AddRange(Sources("*.cs").Where(static source => IsErasureFamily(source.Path, source.Text)));

        Assert.NotEmpty(found);

        return [.. found.DistinctBy(static source => source.Path, StringComparer.Ordinal)];
    }

    private static CompilationUnitSyntax ParseUnit(string source) =>
        CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview)).GetCompilationUnitRoot();

    /// <summary>Every invocation in a file that writes a log line or declares a log template.</summary>
    private static List<InvocationExpressionSyntax> LogCalls(CompilationUnitSyntax root)
    {
        HashSet<string> loggers = LoggerNames(root);

        return [.. root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(invocation => IsLogCall(invocation, loggers))];
    }

    /// <summary>
    /// Whether an invocation writes a log line or declares a log template.
    /// </summary>
    /// <remarks>
    /// An invocation logs when it calls a method of the logging method set (<c>Log…</c>,
    /// <c>BeginScope</c>, <c>LoggerMessage.Define…</c>), whatever its receiver, or when its receiver is a
    /// logger: the static Serilog <c>Log</c>, or anything the file declares with an <c>ILogger</c> type,
    /// whatever the receiver is named. Every method of a logger counts, because <c>ForContext</c> copies an
    /// argument into every later line as surely as a level method does. A logger a file inherits from a base
    /// class is declared elsewhere, which a syntax-only scan cannot see.
    /// </remarks>
    private static bool IsLogCall(InvocationExpressionSyntax invocation, HashSet<string> loggers)
    {
        string name = InvokedName(invocation);

        if (name.StartsWith("Log", StringComparison.Ordinal) || name == "BeginScope")
        {
            return true;
        }

        if (ReceiverOf(invocation) is not { } receiver)
        {
            return false;
        }

        string text = receiver.ToString();

        return (name.StartsWith("Define", StringComparison.Ordinal)
                && (text == "LoggerMessage" || text.EndsWith(".LoggerMessage", StringComparison.Ordinal)))
            || IsLoggerReceiver(receiver, loggers);
    }

    /// <summary>The expression an invocation is called on, through a null-conditional access too.</summary>
    private static ExpressionSyntax? ReceiverOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Expression,
        MemberBindingExpressionSyntax => invocation.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault()?.Expression,
        _ => null,
    };

    /// <summary>
    /// Whether an expression is a logger: the static Serilog <c>Log</c>, a name the file declares with a
    /// logger type, or a call or member access chained on one.
    /// </summary>
    private static bool IsLoggerReceiver(ExpressionSyntax? receiver, HashSet<string> loggers) => receiver switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText == "Log" || loggers.Contains(identifier.Identifier.ValueText),
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == "Log"
            || loggers.Contains(member.Name.Identifier.ValueText)
            || IsLoggerReceiver(member.Expression, loggers),
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax inner } => IsLoggerReceiver(inner.Expression, loggers),
        ParenthesizedExpressionSyntax parenthesized => IsLoggerReceiver(parenthesized.Expression, loggers),
        PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } suppressed => IsLoggerReceiver(suppressed.Operand, loggers),
        CastExpressionSyntax cast => IsLoggerType(cast.Type) || IsLoggerReceiver(cast.Expression, loggers),
        _ => false,
    };

    /// <summary>Whether a type is <c>ILogger</c> or <c>ILogger&lt;T&gt;</c>, Microsoft's or Serilog's, however qualified or nullable.</summary>
    private static bool IsLoggerType(TypeSyntax? type) => type switch
    {
        NullableTypeSyntax nullable => IsLoggerType(nullable.ElementType),
        QualifiedNameSyntax qualified => IsLoggerType(qualified.Right),
        AliasQualifiedNameSyntax alias => IsLoggerType(alias.Name),
        GenericNameSyntax generic => generic.Identifier.ValueText == "ILogger",
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText == "ILogger",
        _ => false,
    };

    /// <summary>
    /// Every name a file declares as a logger: a field, local, property or parameter of a logger type, and a
    /// <c>var</c> local initialized from <c>ForContext</c> or the static <c>Log</c>.
    /// </summary>
    private static HashSet<string> LoggerNames(CompilationUnitSyntax root)
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (ParameterSyntax parameter in root.DescendantNodes().OfType<ParameterSyntax>().Where(static parameter => IsLoggerType(parameter.Type)))
        {
            _ = names.Add(parameter.Identifier.ValueText);
        }

        foreach (PropertyDeclarationSyntax property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Where(static property => IsLoggerType(property.Type)))
        {
            _ = names.Add(property.Identifier.ValueText);
        }

        foreach (VariableDeclarationSyntax declaration in root.DescendantNodes().OfType<VariableDeclarationSyntax>())
        {
            foreach (VariableDeclaratorSyntax variable in declaration.Variables)
            {
                if (IsLoggerType(declaration.Type) || (declaration.Type.IsVar && MakesALogger(variable.Initializer?.Value)))
                {
                    _ = names.Add(variable.Identifier.ValueText);
                }
            }
        }

        return names;
    }

    /// <summary>Whether an initializer is <c>ForContext</c> on a logger, or the static <c>Log</c> itself.</summary>
    private static bool MakesALogger(ExpressionSyntax? initializer) =>
        initializer is not null
        && (initializer.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(static invocation => InvokedName(invocation) == "ForContext")
            || IsLoggerReceiver(initializer, NoLoggers));

    private static string InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => string.Empty,
    };

    /// <summary>How many member accesses in a file spell exactly <paramref name="access"/>, comments and crefs aside.</summary>
    private static int MemberAccesses(string source, string access) =>
        CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))
            .GetCompilationUnitRoot()
            .DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Count(member => string.Equals(member.ToString(), access, StringComparison.Ordinal));

    /// <summary>Whether a file names the Saga store, as the port or as the concrete class.</summary>
    private static bool NamesSagaStore(string source) =>
        source.Contains("SagaMemoryStore", StringComparison.Ordinal);

    private static int InsertAsyncCalls(string source) =>
        CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))
            .GetCompilationUnitRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Count(static invocation => invocation.Expression is MemberAccessExpressionSyntax member
                && member.Name.Identifier.ValueText == "InsertAsync");

    /// <summary>Every authored file under <c>src</c> matching the pattern, whole and repository-relative.</summary>
    private static IEnumerable<(string Path, string Text)> Sources(string pattern)
    {
        string repositoryRoot = NativeSqlCipherTestPaths.RepositoryRoot();

        List<(string Path, string Text)> sources = [];

        foreach (string file in Directory.EnumerateFiles(Path.Combine(repositoryRoot, "src"), pattern, SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            sources.Add((Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/'), File.ReadAllText(file)));
        }

        Assert.NotEmpty(sources);

        return sources;
    }
}
