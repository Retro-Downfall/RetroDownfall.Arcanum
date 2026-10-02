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
/// <para><b>New erasure log lines are content-free.</b> A log template that names an erased item's
/// content, name or key would copy it into application logs, which an erase never reaches.
/// <see cref="ContentFreeLogFiles"/> is closed: every later erasure task appends its new source files
/// to it.</para>
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
    /// Every erasure source file whose log templates this scan holds, relative to
    /// <c>src/RetroDownfall.Arcanum.Infrastructure/</c>. Closed: a new erasure source file joins it.
    /// </summary>
    /// <remarks>
    /// Existing chokepoint owners such as the Lexicon service are not listed. They already log names
    /// under the rule that preceded this one, and the content-free rule covers new log lines.
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

    /// <summary>Serilog's level methods, called statically on <c>Log</c> or on a logger instance.</summary>
    private static readonly HashSet<string> SerilogLevels = new(StringComparer.Ordinal)
    {
        "Verbose",
        "Debug",
        "Information",
        "Warning",
        "Error",
        "Fatal",
        "Write",
    };

    private static readonly HashSet<string> ForbiddenPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Name",
        "Key",
        "NormalizedKey",
        "Content",
        "Fact",
        "Facts",
    };

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
                LogTemplateViolations(File.ReadAllText(path)).Select(violation => $"{file}: {violation}"));
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void The_log_template_scan_flags_a_named_placeholder()
    {
        const string flagged = """
            internal sealed class Fixture(Microsoft.Extensions.Logging.ILogger logger)
            {
                internal void Erase(string name) => logger.LogWarning("Erased {Name}.", name);
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

        Assert.Equal(2, LogTemplateViolations(attribute).Count);

        const string clean = """
            internal sealed class Fixture(Microsoft.Extensions.Logging.ILogger logger)
            {
                internal void Erase(int store) => logger.LogInformation("Erased one item in store {Store}; {NameCount} names.", store, 1);

                internal string Describe(string name) => string.Format("Erased {Name}.", name);
            }
            """;

        Assert.Empty(LogTemplateViolations(clean));
    }

    /// <summary>
    /// Every logging call shape the listed files use, or could, is scanned: Serilog's static and
    /// instance API as well as <c>ILogger</c>, message definitions, scopes, and interpolated
    /// templates, which carry whatever they interpolate into the log whatever the placeholder is named.
    /// </summary>
    [Theory]
    [InlineData("Log.Warning(\"Erased {Name}.\", name);", 1)]
    [InlineData("Serilog.Log.Error(\"Released {Content}.\", name);", 1)]
    [InlineData("global::Serilog.Log.Information(\"Erased {Key}.\", name);", 1)]
    [InlineData("Log.ForContext<Fixture>().Debug(\"Erased {Facts}.\", name);", 1)]
    [InlineData("_log.Verbose(\"Erased {NormalizedKey}.\", name);", 1)]
    [InlineData("Log.Write(LogEventLevel.Warning, \"Erased {Fact}.\", name);", 1)]
    [InlineData("Log.Fatal(\"Erased {name}.\", name);", 1)]
    [InlineData("logger.LogWarning(\"Erased {Name}.\", name);", 1)]
    [InlineData("logger.Log(LogLevel.Warning, \"Erased {Name}.\", name);", 1)]
    [InlineData("_ = logger.BeginScope(\"Erasing {Name}.\", name);", 1)]
    [InlineData("_ = LoggerMessage.Define<string>(LogLevel.Information, new EventId(1), \"Erased {Key}.\");", 1)]
    [InlineData("Log.Information($\"Erased {name}.\");", 1)]
    [InlineData("logger.LogInformation($\"Erased {name}.\");", 1)]
    [InlineData("Log.Warning(\"The erasure key is {KeyState}.\", name);", 0)]
    [InlineData("logger.LogInformation(\"Checkpoint attempt: {WalCheckpointAttempt}.\", name);", 0)]
    [InlineData("_ = string.Format(\"Erased {Name}.\", name);", 0)]
    public void The_log_template_scan_reads_every_logging_call_shape(string call, int expected)
    {
        string source = $$"""
            internal sealed class Fixture(Microsoft.Extensions.Logging.ILogger logger, Serilog.ILogger _log)
            {
                internal void Erase(string name)
                {
                    {{call}}
                }
            }
            """;

        Assert.Equal(expected, LogTemplateViolations(source).Count);
    }

    /// <summary>
    /// Every placeholder the scan forbids in a template passed to a <c>Log…</c> invocation or declared
    /// on a <c>[LoggerMessage]</c> attribute.
    /// </summary>
    private static List<string> LogTemplateViolations(string source)
    {
        CompilationUnitSyntax root = CSharpSyntaxTree
            .ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))
            .GetCompilationUnitRoot();

        List<SyntaxNode> templates =
        [
            .. root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(static invocation => IsLogCall(invocation))
                .Select(static invocation => (SyntaxNode)invocation.ArgumentList),
            .. root.DescendantNodes()
                .OfType<AttributeSyntax>()
                .Where(static attribute => attribute.Name.ToString() is "LoggerMessage" or "LoggerMessageAttribute"
                    || attribute.Name.ToString().EndsWith(".LoggerMessage", StringComparison.Ordinal)
                    || attribute.Name.ToString().EndsWith(".LoggerMessageAttribute", StringComparison.Ordinal))
                .Where(static attribute => attribute.ArgumentList is not null)
                .Select(static attribute => (SyntaxNode)attribute.ArgumentList!),
        ];

        List<string> violations = [];

        foreach (SyntaxNode arguments in templates)
        {
            foreach (LiteralExpressionSyntax literal in arguments.DescendantNodes()
                .OfType<LiteralExpressionSyntax>()
                .Where(static literal => literal.IsKind(SyntaxKind.StringLiteralExpression)))
            {
                foreach (Match match in Placeholder.Matches(literal.Token.ValueText))
                {
                    if (ForbiddenPlaceholders.Contains(match.Groups["name"].Value))
                    {
                        violations.Add($"{match.Value} in \"{literal.Token.ValueText}\"");
                    }
                }
            }

            foreach (InterpolatedStringExpressionSyntax interpolated in arguments.DescendantNodes()
                .OfType<InterpolatedStringExpressionSyntax>())
            {
                violations.Add($"an interpolated string in a log call: {interpolated}");
            }
        }

        return violations;
    }

    /// <summary>
    /// Whether an invocation writes a log line or declares a log template.
    /// </summary>
    /// <remarks>
    /// <c>ILogger</c>'s extension methods (<c>LogWarning</c>, <c>Log</c>, …), Serilog's level methods
    /// on the static <c>Log</c> or on any receiver named for a logger, <c>LoggerMessage.Define…</c>, and
    /// <c>BeginScope</c>. Syntax alone cannot resolve a receiver's type, so a receiver is a logger when
    /// its text names one: reading too many calls as logging is the safe direction to be wrong in.
    /// </remarks>
    private static bool IsLogCall(InvocationExpressionSyntax invocation)
    {
        string name = InvokedName(invocation);

        if (name.StartsWith("Log", StringComparison.Ordinal) || name == "BeginScope")
        {
            return true;
        }

        if (invocation.Expression is not MemberAccessExpressionSyntax member)
        {
            return false;
        }

        string receiver = member.Expression.ToString();

        return (SerilogLevels.Contains(name) && receiver.Contains("log", StringComparison.OrdinalIgnoreCase))
            || (name.StartsWith("Define", StringComparison.Ordinal)
                && (receiver == "LoggerMessage" || receiver.EndsWith(".LoggerMessage", StringComparison.Ordinal)));
    }

    private static string InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
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
