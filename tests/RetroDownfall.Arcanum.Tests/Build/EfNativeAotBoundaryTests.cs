using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Build;

public sealed class EfNativeAotBoundaryTests
{
    private static readonly HashSet<string> TrackedStateOperations =
        new(StringComparer.Ordinal)
        {
            "Add",
            "AddAsync",
            "AddRange",
            "AddRangeAsync",
            "Attach",
            "AttachRange",
            "Remove",
            "RemoveRange",
            "Update",
            "UpdateRange",
        };

    private static readonly HashSet<string> EfQueryBypassOperations =
        new(StringComparer.Ordinal)
        {
            "CompileAsyncQuery",
            "CompileQuery",
            "ExecuteDelete",
            "ExecuteDeleteAsync",
            "ExecuteSqlInterpolated",
            "ExecuteSqlInterpolatedAsync",
            "ExecuteSqlRaw",
            "ExecuteSqlRawAsync",
            "ExecuteUpdate",
            "ExecuteUpdateAsync",
            "FromSql",
            "FromSqlInterpolated",
            "FromSqlRaw",
            "SqlQuery",
            "SqlQueryRaw",
        };

    private static readonly HashSet<string> DbContextQueryOperations =
        new(StringComparer.Ordinal)
        {
            "Find",
            "FindAsync",
        };

    private static readonly HashSet<string> EntityEntryDatabaseReadOperations =
        new(StringComparer.Ordinal)
        {
            "GetDatabaseValues",
            "GetDatabaseValuesAsync",
            "Load",
            "LoadAsync",
            "Query",
            "Reload",
            "ReloadAsync",
        };

    private static readonly HashSet<string> EfQueryApiTypeNames =
        new(StringComparer.Ordinal)
        {
            "EF",
            "EntityFrameworkQueryableExtensions",
            "RelationalDatabaseFacadeExtensions",
            "RelationalQueryableExtensions",
        };

    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview, DocumentationMode.None);

    [Fact]
    public void Shipping_code_uses_entity_framework_only_for_tracked_state_not_queries()
    {
        string repositoryRoot = FindRepositoryRoot();
        HashSet<string> dbSetNames = ReadDbSetNames(repositoryRoot);

        List<string> violations = [];

        foreach (string path in ShippingSourceRoots(repositoryRoot).SelectMany(
            static root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)))
        {
            string source = File.ReadAllText(path);
            string relativePath = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
            violations.AddRange(FindViolations(source, relativePath, dbSetNames));
        }

        Assert.True(
            violations.Count == 0,
            "EF owns the compiled model, tracked entity state, SaveChanges, and the ambient "
            + "transaction. Every application query and set-based operation must use parameterized "
            + "SQLite over the same scoped connection so Native AOT never needs EF's experimental "
            + "runtime query compiler:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void Source_inventory_includes_the_shipping_executable_project()
    {
        string repositoryRoot = FindRepositoryRoot();

        Assert.Contains(
            Path.Combine(repositoryRoot, "src", "RetroDownfall.Arcanum.Cli"),
            ShippingSourceRoots(repositoryRoot),
            StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("_ = db.Find<Session>(id);", "Find")]
    [InlineData("_ = db.FindAsync<Session>(id);", "FindAsync")]
    [InlineData("_ = db.Database.ExecuteSqlRaw(\"DELETE FROM Sessions;\");", "ExecuteSqlRaw")]
    [InlineData("_ = db.Database.ExecuteSqlRawAsync(\"DELETE FROM Sessions;\");", "ExecuteSqlRawAsync")]
    [InlineData("_ = db.Database.ExecuteSqlInterpolated($\"DELETE FROM Sessions WHERE Id = {id}\");", "ExecuteSqlInterpolated")]
    [InlineData("_ = db.Database.ExecuteSqlInterpolatedAsync($\"DELETE FROM Sessions WHERE Id = {id}\");", "ExecuteSqlInterpolatedAsync")]
    [InlineData("db.Entry(id).Reload();", "Reload")]
    [InlineData("_ = db.Entry(id).GetDatabaseValues();", "GetDatabaseValues")]
    [InlineData("db.Entry(id).Collection(\"Entries\").Load();", "Load")]
    [InlineData("_ = db.Entry(id).Collection(\"Entries\").Query();", "Query")]
    public void Guard_rejects_context_query_and_set_based_escape_hatches(
        string statement,
        string expectedOperation)
    {
        string source = $$"""
            class Probe
            {
                void Execute(ArcanumDbContext db, object id)
                {
                    {{statement}}
                }
            }
            """;

        IReadOnlyList<string> violations = FindViolations(
            source,
            "Probe.cs",
            new HashSet<string>(StringComparer.Ordinal) { "Sessions" });

        Assert.Contains(
            violations,
            violation => violation.Contains(expectedOperation, StringComparison.Ordinal));
    }

    [Fact]
    public void Guard_rejects_query_expression_syntax_even_when_the_source_is_hidden_behind_a_helper()
    {
        const string source = """
            class Probe
            {
                void Execute(ArcanumDbContext db)
                {
                    var result =
                        from session in HiddenQuery(db)
                        where session.Id != Guid.Empty
                        select session;
                }
            }
            """;

        IReadOnlyList<string> violations = FindViolations(
            source,
            "Probe.cs",
            new HashSet<string>(StringComparer.Ordinal) { "Sessions" });

        Assert.Contains(
            violations,
            static violation => violation.Contains("query syntax", StringComparison.Ordinal));
    }

    [Fact]
    public void Guard_allows_query_expression_syntax_over_an_in_memory_collection()
    {
        const string source = """
            sealed class Probe
            {
                int[] Run(int[] values) =>
                [
                    .. from value in values
                       where value > 0
                       orderby value
                       select value,
                ];
            }
            """;

        IReadOnlyList<string> violations = FindViolations(
            source,
            "Probe.cs",
            new HashSet<string>(StringComparer.Ordinal) { "Sessions" });

        Assert.Empty(violations);
    }

    [Fact]
    public void Guard_allows_compiled_model_state_tracking_and_parameterized_sqlite()
    {
        const string source = """
            class Probe
            {
                async Task ExecuteAsync(ArcanumDbContext db, Session session, SqliteCommand command)
                {
                    db.Sessions.Add(session);
                    await db.SaveChangesAsync();
                    command.CommandText = "SELECT Id FROM Sessions WHERE Id = $id;";
                    command.Parameters.AddWithValue("$id", session.Id);
                    _ = await command.ExecuteScalarAsync();
                }
            }
            """;

        IReadOnlyList<string> violations = FindViolations(
            source,
            "Probe.cs",
            new HashSet<string>(StringComparer.Ordinal) { "Sessions" });

        Assert.Empty(violations);
    }

    [Fact]
    public void Guard_allows_non_ef_domain_methods_that_share_an_ef_operation_name()
    {
        const string source = "sealed class Probe { void Run(CleanupQueue cleanup) { cleanup.ExecuteDelete(); } }";

        IReadOnlyList<string> violations = FindViolations(
            source,
            "Probe.cs",
            new HashSet<string>(StringComparer.Ordinal) { "Sessions" });

        Assert.Empty(violations);
    }

    [Fact]
    public void Guard_does_not_treat_a_conventional_variable_name_as_a_context_without_type_evidence()
    {
        const string source = "sealed class Probe { void Run(SessionBucket db) { db.Sessions.Clear(); } }";

        IReadOnlyList<string> violations = FindViolations(
            source,
            "Probe.cs",
            new HashSet<string>(StringComparer.Ordinal) { "Sessions" });

        Assert.Empty(violations);
    }

    [Fact]
    public void Guard_keeps_same_named_parameters_isolated_between_methods()
    {
        const string source = """
            sealed class Probe
            {
                void First(ArcanumDbContext db) { }
                void Second(SessionBucket db) { db.Sessions.Clear(); }
            }
            """;

        IReadOnlyList<string> violations = FindViolations(
            source,
            "Probe.cs",
            new HashSet<string>(StringComparer.Ordinal) { "Sessions" });

        Assert.Empty(violations);
    }

    [Fact]
    public void Guard_recognizes_a_context_typed_property_receiver()
    {
        const string source =
            "sealed class Probe { ArcanumDbContext Store { get; } void Run() { _ = Store.Sessions; } }";

        IReadOnlyList<string> violations = FindViolations(
            source,
            "Probe.cs",
            new HashSet<string>(StringComparer.Ordinal) { "Sessions" });

        Assert.Contains(
            violations,
            static violation => violation.Contains("DbSet 'Sessions'", StringComparison.Ordinal));
    }

    [Fact]
    public void Guard_follows_local_context_aliases()
    {
        const string source =
            "sealed class Probe { void Run(ArcanumDbContext db) { var alias = db; _ = alias.Sessions; } }";

        IReadOnlyList<string> violations = FindViolations(
            source,
            "Probe.cs",
            new HashSet<string>(StringComparer.Ordinal) { "Sessions" });

        Assert.Contains(
            violations,
            static violation => violation.Contains("DbSet 'Sessions'", StringComparison.Ordinal));
    }

    [Fact]
    public void Guard_follows_contexts_captured_by_nested_functions()
    {
        const string source =
            "sealed class Probe { void Run(ArcanumDbContext db) { Action read = () => { _ = db.Sessions; }; } }";

        IReadOnlyList<string> violations = FindViolations(
            source,
            "Probe.cs",
            new HashSet<string>(StringComparer.Ordinal) { "Sessions" });

        Assert.Contains(
            violations,
            static violation => violation.Contains("DbSet 'Sessions'", StringComparison.Ordinal));
    }

    [Fact]
    public void Guard_allows_the_tracked_local_view()
    {
        const string source =
            "sealed class Probe { void Run(ArcanumDbContext db) { _ = db.Sessions.Local; } }";

        IReadOnlyList<string> violations = FindViolations(
            source,
            "Probe.cs",
            new HashSet<string>(StringComparer.Ordinal) { "Sessions" });

        Assert.Empty(violations);
    }

    private static IReadOnlyList<string> FindViolations(
        string source,
        string path,
        HashSet<string> dbSetNames)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            source,
            ParseOptions,
            path);
        SyntaxNode root = tree.GetRoot();
        Dictionary<SyntaxNode, HashSet<string>> contextNamesByScope =
            new(ReferenceEqualityComparer.Instance);
        List<string> violations = [];

        HashSet<string> ContextNames(SyntaxNode node)
        {
            SyntaxNode scope = ContextScope(node);

            if (!contextNamesByScope.TryGetValue(scope, out HashSet<string>? names))
            {
                names = ReadDbContextNames(scope);
                contextNamesByScope.Add(scope, names);
            }

            return names;
        }

        foreach (TypeSyntax type in root.DescendantNodes().OfType<TypeSyntax>())
        {
            if (type.ToString().Contains("IQueryable", StringComparison.Ordinal))
            {
                violations.Add(Location(type, "IQueryable is a runtime-composed query surface"));
            }
        }

        foreach (MemberAccessExpressionSyntax access in root.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(access => dbSetNames.Contains(access.Name.Identifier.ValueText))
            .Where(access => IsDbContextReceiver(access.Expression, ContextNames(access))))
        {
            if (IsTrackedStateOperation(access))
            {
                continue;
            }

            violations.Add(Location(
                access,
                $"DbSet '{access.Name.Identifier.ValueText}' escapes the EF tracked-state lane"));
        }

        foreach (QueryExpressionSyntax query in root.DescendantNodes()
                     .OfType<QueryExpressionSyntax>()
                     .Where(query => IsEfQueryExpression(
                         query,
                         ContextNames(query),
                         dbSetNames)))
        {
            violations.Add(Location(
                query,
                "LINQ query syntax can hide a runtime-composed EF query"));
        }

        foreach (InvocationExpressionSyntax invocation in root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>())
        {
            HashSet<string> dbContextNames = ContextNames(invocation);

            if (invocation.Expression is MemberAccessExpressionSyntax member
                && member.Name is GenericNameSyntax { Identifier.ValueText: "Set" }
                && IsDbContextReceiver(member.Expression, dbContextNames))
            {
                violations.Add(Location(
                    invocation,
                    "DbContext.Set<T>() can hide an EF query from the closed DbSet inventory"));
            }

            if (invocation.Expression is MemberAccessExpressionSyntax bypass
                && EfQueryBypassOperations.Contains(bypass.Name.Identifier.ValueText)
                && IsEfQueryApiReceiver(
                    bypass.Expression,
                    dbContextNames,
                    dbSetNames))
            {
                violations.Add(Location(
                    invocation,
                    $"EF query API '{bypass.Name.Identifier.ValueText}' bypasses the tracked-state lane"));
            }

            if (invocation.Expression is MemberAccessExpressionSyntax contextQuery
                && DbContextQueryOperations.Contains(contextQuery.Name.Identifier.ValueText)
                && IsDbContextReceiver(contextQuery.Expression, dbContextNames))
            {
                violations.Add(Location(
                    invocation,
                    $"DbContext query API '{contextQuery.Name.Identifier.ValueText}' bypasses the tracked-state lane"));
            }

            if (invocation.Expression is MemberAccessExpressionSyntax entryQuery
                && EntityEntryDatabaseReadOperations.Contains(entryQuery.Name.Identifier.ValueText)
                && ContainsDbContextEntry(entryQuery.Expression, dbContextNames))
            {
                violations.Add(Location(
                    invocation,
                    $"EF entry query API '{entryQuery.Name.Identifier.ValueText}' bypasses the tracked-state lane"));
            }
        }

        return violations;
    }

    private static bool IsTrackedStateOperation(MemberAccessExpressionSyntax dbSetAccess) =>
        dbSetAccess.Parent is MemberAccessExpressionSyntax operation
        && (operation.Name.Identifier.ValueText == "Local"
            || (TrackedStateOperations.Contains(operation.Name.Identifier.ValueText)
                && operation.Parent is InvocationExpressionSyntax));

    private static bool IsEfQueryExpression(
        QueryExpressionSyntax query,
        HashSet<string> dbContextNames,
        HashSet<string> dbSetNames)
    {
        ExpressionSyntax source = query.FromClause.Expression;

        if (source is MemberAccessExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax localDbSet,
                Name.Identifier.ValueText: "Local",
            }
            && dbSetNames.Contains(localDbSet.Name.Identifier.ValueText)
            && IsDbContextReceiver(localDbSet.Expression, dbContextNames))
        {
            return false;
        }

        if (source.DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>()
            .Any(identifier => dbContextNames.Contains(identifier.Identifier.ValueText)))
        {
            return true;
        }

        return source.DescendantNodesAndSelf()
            .OfType<MemberAccessExpressionSyntax>()
            .Any(access => dbSetNames.Contains(access.Name.Identifier.ValueText)
                && IsDbContextReceiver(access.Expression, dbContextNames));
    }

    private static HashSet<string> ReadDbContextNames(SyntaxNode scope)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        SyntaxNode? outerScope = scope.Ancestors().FirstOrDefault(IsContextScope);

        if (outerScope is not null)
        {
            names.UnionWith(ReadDbContextNames(outerScope));
        }

        TypeDeclarationSyntax? containingType = scope.AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();

        if (containingType is not null)
        {
            foreach (FieldDeclarationSyntax field in containingType.Members
                         .OfType<FieldDeclarationSyntax>()
                         .Where(static field => IsDbContextType(field.Declaration.Type)))
            {
                foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                {
                    _ = names.Add(variable.Identifier.ValueText);
                }
            }

            foreach (PropertyDeclarationSyntax property in containingType.Members
                         .OfType<PropertyDeclarationSyntax>()
                         .Where(static property => IsDbContextType(property.Type)))
            {
                _ = names.Add(property.Identifier.ValueText);
            }

            if (containingType is ClassDeclarationSyntax
                {
                    ParameterList.Parameters: SeparatedSyntaxList<ParameterSyntax> typeParameters,
                })
            {
                foreach (ParameterSyntax parameter in typeParameters.Where(
                             static parameter => IsDbContextType(parameter.Type)))
                {
                    _ = names.Add(parameter.Identifier.ValueText);
                }
            }
        }

        foreach (ParameterSyntax parameter in scope.DescendantNodesAndSelf()
                     .OfType<ParameterSyntax>()
                     .Where(parameter => ContextScope(parameter) == scope))
        {
            if (IsDbContextType(parameter.Type))
            {
                _ = names.Add(parameter.Identifier.ValueText);
            }
        }

        foreach (VariableDeclarationSyntax declaration in scope.DescendantNodesAndSelf()
                     .OfType<VariableDeclarationSyntax>()
                     .Where(declaration => ContextScope(declaration) == scope))
        {
            if (!IsDbContextType(declaration.Type))
            {
                continue;
            }

            foreach (VariableDeclaratorSyntax variable in declaration.Variables)
            {
                _ = names.Add(variable.Identifier.ValueText);
            }
        }

        bool discoveredAlias;

        do
        {
            discoveredAlias = false;

            foreach (VariableDeclaratorSyntax variable in scope.DescendantNodesAndSelf()
                         .OfType<VariableDeclaratorSyntax>()
                         .Where(variable => ContextScope(variable) == scope)
                         .Where(static variable => variable.Initializer is not null))
            {
                if (IsDbContextReceiver(variable.Initializer!.Value, names))
                {
                    discoveredAlias |= names.Add(variable.Identifier.ValueText);
                }
            }

            foreach (AssignmentExpressionSyntax assignment in scope.DescendantNodesAndSelf()
                         .OfType<AssignmentExpressionSyntax>()
                         .Where(assignment => ContextScope(assignment) == scope))
            {
                if (assignment.Left is IdentifierNameSyntax alias
                    && IsDbContextReceiver(assignment.Right, names))
                {
                    discoveredAlias |= names.Add(alias.Identifier.ValueText);
                }
            }
        }

        while (discoveredAlias);

        return names;
    }

    private static SyntaxNode ContextScope(SyntaxNode node) =>
        node.AncestorsAndSelf().FirstOrDefault(IsContextScope)
        ?? node.SyntaxTree.GetRoot();

    private static bool IsContextScope(SyntaxNode node) =>
        node is BaseMethodDeclarationSyntax
            or AccessorDeclarationSyntax
            or LocalFunctionStatementSyntax
            or AnonymousFunctionExpressionSyntax
            or PropertyDeclarationSyntax
            or FieldDeclarationSyntax;

    private static bool IsDbContextType(TypeSyntax? type) =>
        type?.ToString().EndsWith("ArcanumDbContext", StringComparison.Ordinal) == true;

    private static bool IsDbContextReceiver(
        ExpressionSyntax expression,
        HashSet<string> dbContextNames) =>
        expression switch
        {
            IdentifierNameSyntax identifier => dbContextNames.Contains(identifier.Identifier.ValueText),
            MemberAccessExpressionSyntax
            {
                Expression: ThisExpressionSyntax,
                Name: IdentifierNameSyntax identifier,
            } => dbContextNames.Contains(identifier.Identifier.ValueText),
            ParenthesizedExpressionSyntax parenthesized =>
                IsDbContextReceiver(parenthesized.Expression, dbContextNames),
            CastExpressionSyntax cast =>
                IsDbContextType(cast.Type)
                || IsDbContextReceiver(cast.Expression, dbContextNames),
            _ => false,
        };

    private static bool IsEfQueryApiReceiver(
        ExpressionSyntax expression,
        HashSet<string> dbContextNames,
        HashSet<string> dbSetNames)
    {
        if (expression is IdentifierNameSyntax identifier
            && EfQueryApiTypeNames.Contains(identifier.Identifier.ValueText))
        {
            return true;
        }

        if (expression is MemberAccessExpressionSyntax
            {
                Expression: ExpressionSyntax context,
                Name.Identifier.ValueText: "Database",
            }
            && IsDbContextReceiver(context, dbContextNames))
        {
            return true;
        }

        return expression.DescendantNodesAndSelf()
            .OfType<MemberAccessExpressionSyntax>()
            .Any(access => dbSetNames.Contains(access.Name.Identifier.ValueText)
                && IsDbContextReceiver(access.Expression, dbContextNames));
    }

    private static bool ContainsDbContextEntry(
        ExpressionSyntax expression,
        HashSet<string> dbContextNames) =>
        expression.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Any(invocation => invocation.Expression is MemberAccessExpressionSyntax
                {
                    Expression: ExpressionSyntax context,
                    Name.Identifier.ValueText: "Entry",
                }
                && IsDbContextReceiver(context, dbContextNames));

    private static HashSet<string> ReadDbSetNames(string repositoryRoot)
    {
        string dbContextPath = Path.Combine(
            repositoryRoot,
            "src",
            "RetroDownfall.Arcanum.Infrastructure",
            "Data",
            "ArcanumDbContext.cs");
        SyntaxNode root = CSharpSyntaxTree.ParseText(
            File.ReadAllText(dbContextPath),
            ParseOptions,
            dbContextPath).GetRoot();

        return root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .Where(static property => property.Type.DescendantNodesAndSelf()
                .OfType<GenericNameSyntax>()
                .Any(static generic => generic.Identifier.ValueText == "DbSet"))
            .Select(static property => property.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string[] ShippingSourceRoots(string repositoryRoot) =>
    [
        Path.Combine(repositoryRoot, "src", "RetroDownfall.Arcanum.Infrastructure"),
        Path.Combine(repositoryRoot, "src", "RetroDownfall.Arcanum.Api"),
        Path.Combine(repositoryRoot, "src", "RetroDownfall.Arcanum.Cli"),
    ];

    private static string Location(SyntaxNode node, string reason)
    {
        FileLinePositionSpan span = node.GetLocation().GetLineSpan();

        return $"{span.Path}:{span.StartLinePosition.Line + 1}: {reason}";
    }

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
}
