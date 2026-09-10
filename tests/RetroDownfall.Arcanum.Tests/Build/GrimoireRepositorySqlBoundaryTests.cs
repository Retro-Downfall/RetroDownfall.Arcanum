using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Build;

public sealed class GrimoireRepositorySqlBoundaryTests
{
    private static readonly IReadOnlyDictionary<string, DirectSqlAllowance[]> DirectSqlEscapes =
        new Dictionary<string, DirectSqlAllowance[]>(StringComparer.Ordinal)
        {
            // Campaign registration owns one deliberately short, admitted BEGIN IMMEDIATE
            // transaction. The connection lifecycle and busy-timeout PRAGMAs belong to it.
            ["CampaignRepository.cs"] =
            [
                new("AddAsync", "GetDbConnection", 1),
                new("AddAsync", "OpenConnectionAsync", 1),
                new("AddAsync", "CloseAsync", 1),
                new("AddAsync", "CloseConnectionAsync", 1),
                new("AddWithinImmediateTransactionAsync", "BeginTransaction", 1),
                new("ReadBusyTimeout", "CreateCommand", 1),
                new("SetBusyTimeout", "CreateCommand", 1),
            ],
            // Turn commit owns one admitted BEGIN IMMEDIATE transaction. These commands must
            // participate in that transaction before an EF ambient transaction exists.
            ["GrimoireRepository.TurnCommit.cs"] =
            [
                new("CommitWithinImmediateTransactionAsync", "GetDbConnection", 1),
                new("CommitWithinImmediateTransactionAsync", "BeginTransaction", 1),
                new("SeedSessionCapacityRowAsync", "CreateCommand", 1),
                new("ReadFinalizationGuardAsync", "CreateCommand", 1),
                new("InsertFinalizationGuardAsync", "CreateCommand", 1),
            ],
            // Session usage accounting owns one admitted BEGIN IMMEDIATE transaction so its exact
            // decimal read-modify-write cannot interleave. There is no EF ambient transaction for
            // GrimoireSqlCommandFactory to join; both commands explicitly use this owned transaction.
            ["GrimoireRepository.cs"] =
            [
                new("IncrementSessionTokensAndCostWithinImmediateTransactionAsync", "GetDbConnection", 1),
                new("IncrementSessionTokensAndCostWithinImmediateTransactionAsync", "BeginTransaction", 1),
                new("IncrementSessionTokensAndCostWithinImmediateTransactionAsync", "CreateCommand", 2),
            ],
            // Cancellation and commit-ambiguity classification require two deliberately fresh,
            // admitted read-only connections rather than the possibly failed scoped connection.
            ["SessionEntryPersistence.cs"] =
            [
                new("ReadProbeOnFreshConnectionAsync", "CreateCommand", 1),
                new("ReadReceiptOnFreshConnectionAsync", "CreateCommand", 1),
            ],
        };

    [Fact]
    public void Every_context_backed_repository_and_grimoire_partial_has_no_unreviewed_sql_escape()
    {
        string repositoryDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Infrastructure",
            "Repositories");
        RepositorySource[] sources = ReadRepositorySources(repositoryDirectory);

        string[] violations = FindBoundaryViolations(sources, DirectSqlEscapes);

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("NewRepository.cs", "sealed class NewRepository(ArcanumDbContext db) { void Run() { db.Database.GetDbConnection().CreateCommand(); } }")]
    [InlineData("ContextHelper.cs", "sealed class ContextHelper(ArcanumDbContext db) { void Run() { db.Database.GetDbConnection(); } }")]
    [InlineData("GrimoireRepository.NewPath.cs", "partial class GrimoireRepository { void Run() { connection.CreateCommand(); } }")]
    public void Detector_rejects_new_context_backed_files_and_grimoire_partials_that_bypass_the_factory(
        string fileName,
        string source)
    {
        string[] violations = FindBoundaryViolations(
            [new RepositorySource(fileName, source)],
            EmptyEscapes());

        Assert.NotEmpty(violations);
    }

    [Fact]
    public void Detector_rejects_direct_sql_construction()
    {
        const string source =
            "sealed class NewRepository { void Run() { _ = new Microsoft.Data.Sqlite.SqliteConnection(); } }";

        string[] violations = FindBoundaryViolations(
            [new RepositorySource("NewRepository.cs", source)],
            EmptyEscapes());

        Assert.Contains(violations, static violation => violation.Contains("SqliteConnection", StringComparison.Ordinal));
    }

    [Fact]
    public void Detector_rejects_a_transaction_on_a_declaration_pattern_connection()
    {
        const string source =
            "partial class GrimoireRepository { void Run(object value) { "
            + "if (value is not SqliteConnection connection) { return; } "
            + "connection.BeginTransaction(); } }";

        string[] violations = FindBoundaryViolations(
            [new RepositorySource("GrimoireRepository.Pattern.cs", source)],
            EmptyEscapes());

        Assert.Contains(
            violations,
            static violation => violation.Contains(
                "Run.BeginTransaction",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Detector_rejects_stale_escape_allowances()
    {
        IReadOnlyDictionary<string, DirectSqlAllowance[]> escapes =
            new Dictionary<string, DirectSqlAllowance[]>(StringComparer.Ordinal)
            {
                ["RemovedRepository.cs"] = [new("Run", "CreateCommand", 1)],
            };

        string[] violations = FindBoundaryViolations(
            [new RepositorySource("TrackedRepository.cs", "sealed class TrackedRepository { }")],
            escapes);

        Assert.Contains(
            violations,
            static violation => violation.Contains("RemovedRepository.cs", StringComparison.Ordinal)
                && violation.Contains("stale", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Detector_allows_a_new_tracked_state_only_repository_without_a_factory_call()
    {
        const string source =
            "sealed class TrackedRepository(ArcanumDbContext db) { void Add(Entity value) { db.Add(value); } }";

        string[] violations = FindBoundaryViolations(
            [new RepositorySource("TrackedRepository.cs", source)],
            EmptyEscapes());

        Assert.Empty(violations);
    }

    [Fact]
    public void Filesystem_inventory_finds_repository_files_in_future_nested_directories()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-repository-boundary-{Guid.NewGuid():N}");
        string nested = Path.Combine(root, "Future");

        try
        {
            Directory.CreateDirectory(nested);
            File.WriteAllText(
                Path.Combine(nested, "NestedRepository.cs"),
                "sealed class NestedRepository(ArcanumDbContext db) { void Run() { db.Database.GetDbConnection(); } }");

            RepositorySource[] sources = ReadRepositorySources(root);
            string[] violations = FindBoundaryViolations(sources, EmptyEscapes());

            Assert.Contains(
                violations,
                static violation => violation.Contains("Future/NestedRepository.cs", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string[] FindBoundaryViolations(
        IReadOnlyCollection<RepositorySource> sources,
        IReadOnlyDictionary<string, DirectSqlAllowance[]> directSqlEscapes)
    {
        AnalyzedRepositorySource[] candidates = sources
            .Select(static source => new AnalyzedRepositorySource(
                source.FileName,
                CSharpSyntaxTree.ParseText(source.Source).GetCompilationUnitRoot()))
            .Where(static source => IsBoundarySource(source.FileName, source.Root))
            .OrderBy(static source => source.FileName, StringComparer.Ordinal)
            .ToArray();
        HashSet<string> candidateNames = candidates
            .Select(static source => source.FileName)
            .ToHashSet(StringComparer.Ordinal);
        List<string> violations = [];

        foreach (string staleFileName in directSqlEscapes.Keys
                     .Where(fileName => !candidateNames.Contains(fileName))
                     .Order(StringComparer.Ordinal))
        {
            violations.Add($"{staleFileName}: stale direct-SQL escape allowance has no matching boundary file.");
        }

        foreach (AnalyzedRepositorySource candidate in candidates)
        {
            DirectSqlAllowance[] allowances = directSqlEscapes.TryGetValue(candidate.FileName, out DirectSqlAllowance[]? configured)
                ? configured
                : [];

            AddInvocationViolations(candidate, allowances, violations);
            AddConstructionViolations(candidate, violations);
        }

        return [.. violations];
    }

    private static bool IsBoundarySource(string fileName, CompilationUnitSyntax root) =>
        fileName.EndsWith("Repository.cs", StringComparison.Ordinal)
        || fileName.StartsWith("GrimoireRepository.", StringComparison.Ordinal)
        || root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Any(static identifier => identifier.Identifier.ValueText == "ArcanumDbContext");

    private static void AddInvocationViolations(
        AnalyzedRepositorySource candidate,
        IReadOnlyCollection<DirectSqlAllowance> allowances,
        ICollection<string> violations)
    {
        HashSet<string> sqliteConnectionIdentifiers = FindSqliteConnectionIdentifiers(candidate.Root);
        DirectSqlCall[] calls = candidate.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => IsDirectSqlInvocation(invocation, sqliteConnectionIdentifiers))
            .Select(static invocation => new DirectSqlCall(
                ContainingMemberName(invocation),
                InvocationName(invocation)
                    ?? throw new InvalidOperationException("A guarded invocation must have a name.")))
            .ToArray();

        foreach (IGrouping<(string MemberName, string InvocationName), DirectSqlAllowance> duplicate in allowances
                     .GroupBy(static allowance => (allowance.MemberName, allowance.InvocationName))
                     .Where(static group => group.Count() > 1))
        {
            violations.Add(
                $"{candidate.FileName}: duplicate allowance for "
                + $"{duplicate.Key.MemberName}.{duplicate.Key.InvocationName}.");
        }

        foreach (DirectSqlAllowance allowance in allowances)
        {
            int actualCount = calls.Count(call =>
                call.MemberName == allowance.MemberName
                && call.InvocationName == allowance.InvocationName);

            if (allowance.Count <= 0 || actualCount != allowance.Count)
            {
                violations.Add(
                    $"{candidate.FileName}: {allowance.MemberName}.{allowance.InvocationName} "
                    + $"allows exactly {allowance.Count} call(s), found {actualCount}.");
            }
        }

        foreach (IGrouping<(string MemberName, string InvocationName), DirectSqlCall> unreviewed in calls
                     .Where(call => !allowances.Any(allowance =>
                         allowance.MemberName == call.MemberName
                         && allowance.InvocationName == call.InvocationName))
                     .GroupBy(static call => (call.MemberName, call.InvocationName)))
        {
            violations.Add(
                $"{candidate.FileName}: unreviewed direct-SQL escape "
                + $"{unreviewed.Key.MemberName}.{unreviewed.Key.InvocationName} "
                + $"appears {unreviewed.Count()} time(s).");
        }
    }

    private static HashSet<string> FindSqliteConnectionIdentifiers(CompilationUnitSyntax root)
    {
        IEnumerable<string> variables = root.DescendantNodes()
            .OfType<VariableDeclarationSyntax>()
            .Where(static declaration => IsSqliteConnectionType(declaration.Type))
            .SelectMany(static declaration => declaration.Variables)
            .Select(static variable => variable.Identifier.ValueText);
        IEnumerable<string> parameters = root.DescendantNodes()
            .OfType<ParameterSyntax>()
            .Where(static parameter => parameter.Type is not null && IsSqliteConnectionType(parameter.Type))
            .Select(static parameter => parameter.Identifier.ValueText);
        IEnumerable<string> declarationPatterns = root.DescendantNodes()
            .OfType<DeclarationPatternSyntax>()
            .Where(static pattern => IsSqliteConnectionType(pattern.Type))
            .SelectMany(static pattern => pattern.Designation
                .DescendantNodesAndSelf()
                .OfType<SingleVariableDesignationSyntax>())
            .Select(static designation => designation.Identifier.ValueText);

        return variables
            .Concat(parameters)
            .Concat(declarationPatterns)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsSqliteConnectionType(TypeSyntax type) =>
        type.ToString().Equals("SqliteConnection", StringComparison.Ordinal)
        || type.ToString().EndsWith(".SqliteConnection", StringComparison.Ordinal);

    private static bool IsDirectSqlInvocation(
        InvocationExpressionSyntax invocation,
        IReadOnlySet<string> sqliteConnectionIdentifiers)
    {
        string? invocationName = InvocationName(invocation);

        if (invocationName is "GetDbConnection"
            or "OpenConnection"
            or "OpenConnectionAsync"
            or "CloseConnection"
            or "CloseConnectionAsync"
            or "CreateCommand"
            or "CreateBatch")
        {
            return true;
        }

        if (invocationName is not ("Open" or "OpenAsync" or "Close" or "CloseAsync"
            or "BeginTransaction" or "BeginTransactionAsync"))
        {
            return false;
        }

        return invocation.Expression is MemberAccessExpressionSyntax member
            && ReceiverIdentifier(member.Expression) is string receiver
            && sqliteConnectionIdentifiers.Contains(receiver);
    }

    private static string? ReceiverIdentifier(ExpressionSyntax expression) =>
        expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            _ => null,
        };

    private static string? InvocationName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            _ => null,
        };

    private static string ContainingMemberName(InvocationExpressionSyntax invocation) =>
        invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()
            ?.Identifier.ValueText
        ?? invocation.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault()
            ?.Identifier.ValueText
        ?? "<unknown>";

    private static void AddConstructionViolations(
        AnalyzedRepositorySource candidate,
        ICollection<string> violations)
    {
        foreach (ObjectCreationExpressionSyntax creation in candidate.Root.DescendantNodes()
                     .OfType<ObjectCreationExpressionSyntax>())
        {
            string typeName = creation.Type.ToString();

            if (typeName.Equals("SqliteCommand", StringComparison.Ordinal)
                || typeName.EndsWith(".SqliteCommand", StringComparison.Ordinal)
                || typeName.Equals("SqliteConnection", StringComparison.Ordinal)
                || typeName.EndsWith(".SqliteConnection", StringComparison.Ordinal))
            {
                violations.Add(
                    $"{candidate.FileName}: direct construction of {typeName} is not allowed.");
            }
        }
    }

    private static IReadOnlyDictionary<string, DirectSqlAllowance[]> EmptyEscapes() =>
        new Dictionary<string, DirectSqlAllowance[]>(StringComparer.Ordinal);

    private static RepositorySource[] ReadRepositorySources(string repositoryDirectory) =>
        Directory
            .EnumerateFiles(repositoryDirectory, "*.cs", SearchOption.AllDirectories)
            .Select(path => new RepositorySource(
                Path.GetRelativePath(repositoryDirectory, path).Replace('\\', '/'),
                File.ReadAllText(path)))
            .ToArray();

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException("The test source path has no directory."));

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed record RepositorySource(string FileName, string Source);

    private sealed record AnalyzedRepositorySource(string FileName, CompilationUnitSyntax Root);

    private sealed record DirectSqlAllowance(string MemberName, string InvocationName, int Count);

    private sealed record DirectSqlCall(string MemberName, string InvocationName);
}
