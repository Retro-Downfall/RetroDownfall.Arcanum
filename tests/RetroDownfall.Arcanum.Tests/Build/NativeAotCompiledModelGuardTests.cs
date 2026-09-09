using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Build;

public sealed class NativeAotCompiledModelGuardTests
{
    [Fact]
    public void Every_compiled_model_type_mapping_closes_its_default_comparer_generic()
    {
        string repositoryRoot = FindRepositoryRoot();
        (string File, string Mapping, string ClrType)[] expectedMappings =
        [
            ("Generated/NativeAotSqliteTypeMappings.cs", "SqliteGuidTypeMapping", "Guid"),
            ("Generated/NativeAotSqliteTypeMappings.cs", "SqliteStringTypeMapping", "string"),
            ("Generated/NativeAotSqliteTypeMappings.cs", "SqliteDecimalTypeMapping", "decimal"),
            ("Generated/NativeAotSqliteTypeMappings.cs", "SqliteDateTimeTypeMapping", "DateTime"),
            ("Generated/NativeAotSqliteTypeMappings.cs", "SqliteDateTimeOffsetTypeMapping", "DateTimeOffset"),
            ("Data/UtcInstantTypeMappings.cs", "UtcDateTimeTypeMapping", "DateTime"),
            ("Data/UtcInstantTypeMappings.cs", "UtcDateTimeOffsetTypeMapping", "DateTimeOffset"),
        ];

        foreach ((string relativeFile, string mapping, string clrType) in expectedMappings)
        {
            string path = Path.Combine(
                repositoryRoot,
                "src",
                "RetroDownfall.Arcanum.Infrastructure",
                relativeFile);

            Assert.True(
                File.Exists(path),
                $"{relativeFile} must close the {mapping} comparer generic for Native AOT.");

            CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(File.ReadAllText(path))
                .GetCompilationUnitRoot();

            Assert.True(
                HasClosedGenericComparerBoundary(root, mapping, clrType),
                $"{relativeFile} must construct {mapping} through a closed {clrType} comparer "
                + "boundary so Native AOT never uses MakeGenericMethod().");
        }
    }

    [Theory]
    [InlineData("EntryEntityType.cs", "MessageRole", "ForMessageRole")]
    [InlineData("CampaignEntityType.cs", "WorkspaceType", "ForWorkspaceType")]
    public void Enum_mappings_use_named_scoped_ef10_aot_compatibility_boundaries(
        string generatedFile,
        string enumType,
        string boundaryName)
    {
        string generatedDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Infrastructure",
            "Generated");
        string mappingSource = File.ReadAllText(Path.Combine(
            generatedDirectory,
            "NativeAotSqliteTypeMappings.cs"));
        CompilationUnitSyntax mappingRoot = CSharpSyntaxTree.ParseText(mappingSource)
            .GetCompilationUnitRoot();
        MethodDeclarationSyntax boundary = Assert.Single(
            mappingRoot.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText.Equals(boundaryName, StringComparison.Ordinal));
        string boundarySource = boundary.NormalizeWhitespace().ToFullString();

        Assert.Contains(
            $"NativeAotSqliteInt32EnumTypeMapping<{enumType}>.Default",
            boundarySource,
            StringComparison.Ordinal);
        Assert.Contains("UnconditionalSuppressMessage", boundarySource, StringComparison.Ordinal);
        Assert.Contains("\"IL3050", boundarySource, StringComparison.Ordinal);
        Assert.Contains("System.Enum.GetValues(System.Type)", boundarySource, StringComparison.Ordinal);

        string entitySource = File.ReadAllText(Path.Combine(generatedDirectory, generatedFile));

        Assert.Contains(
            $"NativeAotSqliteEnumTypeMappings.{boundaryName}()",
            entitySource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"NativeAotSqliteInt32EnumTypeMapping<{enumType}>.Default",
            entitySource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_mapped_entity_uses_its_generated_native_aot_unsafe_accessors()
    {
        string generatedDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Infrastructure",
            "Generated");
        Dictionary<string, string> generatedFiles = Directory
            .EnumerateFiles(generatedDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .ToDictionary(
                static path => Path.GetFileName(path)
                    ?? throw new InvalidOperationException("A generated file path has no file name."),
                File.ReadAllText,
                StringComparer.Ordinal);

        IReadOnlyList<string> violations = NativeAotCompiledModelGuard.FindViolations(
            generatedFiles,
            ReadMappedEntityNames());

        Assert.True(
            violations.Count == 0,
            "The checked-in EF model was not generated with `dotnet ef dbcontext optimize "
            + "--nativeaot`; regenerate it with the pinned local tool:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void Guard_rejects_a_missing_unsafe_accessor_file()
    {
        Dictionary<string, string> generatedFiles = new(StringComparer.Ordinal)
        {
            ["SessionEntityType.cs"] = "public class SessionEntityType { }",
        };

        IReadOnlyList<string> violations = NativeAotCompiledModelGuard.FindViolations(generatedFiles);

        Assert.Contains(
            violations,
            static violation => violation.Contains("SessionUnsafeAccessors.cs is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Guard_rejects_a_mapped_entity_when_both_generated_files_are_missing()
    {
        Dictionary<string, string> generatedFiles = new(StringComparer.Ordinal)
        {
            ["SessionEntityType.cs"] = "SessionUnsafeAccessors.Id(instance);",
            ["SessionUnsafeAccessors.cs"] = "[UnsafeAccessor] static extern ref int Id(Session value);",
        };

        IReadOnlyList<string> violations = NativeAotCompiledModelGuard.FindViolations(
            generatedFiles,
            ["Entry", "Session"]);

        Assert.Contains(
            violations,
            static violation => violation.Contains("EntryEntityType.cs is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Guard_rejects_an_entity_file_that_does_not_use_its_accessors()
    {
        Dictionary<string, string> generatedFiles = new(StringComparer.Ordinal)
        {
            ["SessionEntityType.cs"] = "public class SessionEntityType { }",
            ["SessionUnsafeAccessors.cs"] = "[UnsafeAccessor(UnsafeAccessorKind.Field)] public static extern ref int Id(Session value);",
        };

        IReadOnlyList<string> violations = NativeAotCompiledModelGuard.FindViolations(generatedFiles);

        Assert.Contains(
            violations,
            static violation => violation.Contains("SessionEntityType.cs does not use SessionUnsafeAccessors", StringComparison.Ordinal));
    }

    [Fact]
    public void Guard_accepts_a_native_aot_entity_and_accessor_pair()
    {
        Dictionary<string, string> generatedFiles = new(StringComparer.Ordinal)
        {
            ["SessionEntityType.cs"] = "SessionUnsafeAccessors.Id(instance);",
            ["SessionUnsafeAccessors.cs"] = "[UnsafeAccessor(UnsafeAccessorKind.Field)] public static extern ref int Id(Session value);",
        };

        IReadOnlyList<string> violations = NativeAotCompiledModelGuard.FindViolations(generatedFiles);

        Assert.Empty(violations);
    }

    [Fact]
    public void Guard_does_not_accept_native_aot_markers_that_exist_only_in_comments()
    {
        Dictionary<string, string> generatedFiles = new(StringComparer.Ordinal)
        {
            ["SessionEntityType.cs"] = "public class SessionEntityType { } // SessionUnsafeAccessors.Id(instance);",
            ["SessionUnsafeAccessors.cs"] = "// [UnsafeAccessor(UnsafeAccessorKind.Field)]",
        };

        IReadOnlyList<string> violations = NativeAotCompiledModelGuard.FindViolations(generatedFiles);

        Assert.Contains(
            violations,
            static violation => violation.Contains("contains no generated UnsafeAccessor member", StringComparison.Ordinal));
        Assert.Contains(
            violations,
            static violation => violation.Contains("does not use SessionUnsafeAccessors", StringComparison.Ordinal));
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

    private static string[] ReadMappedEntityNames()
    {
        string contextPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Infrastructure",
            "Data",
            "ArcanumDbContext.cs");
        CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(File.ReadAllText(contextPath))
            .GetCompilationUnitRoot();

        return root.DescendantNodes()
            .OfType<GenericNameSyntax>()
            .Where(static generic => generic.Identifier.ValueText is "DbSet" or "Entity")
            .Where(static generic => generic.TypeArgumentList.Arguments.Count == 1)
            .Select(static generic => generic.TypeArgumentList.Arguments[0]
                .DescendantNodesAndSelf()
                .OfType<SimpleNameSyntax>()
                .Last()
                .Identifier.ValueText)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool HasClosedGenericComparerBoundary(
        CompilationUnitSyntax root,
        string mappingName,
        string clrType)
    {
        ClassDeclarationSyntax? mapping = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .SingleOrDefault(type => type.Identifier.ValueText.Equals(mappingName, StringComparison.Ordinal));

        if (mapping is null)
        {
            return false;
        }

        string? boundaryName = mapping.DescendantNodes()
            .OfType<GenericNameSyntax>()
            .Where(static generic => generic.Identifier.ValueText is
                "WithClosedComparers" or "NativeAotUtcInstantTypeMapping")
            .Where(static generic => generic.TypeArgumentList.Arguments.Count == 1)
            .Where(generic => generic.TypeArgumentList.Arguments[0]
                .ToString()
                .Equals(clrType, StringComparison.Ordinal))
            .Select(static generic => generic.Identifier.ValueText)
            .SingleOrDefault();

        if (boundaryName is null)
        {
            return false;
        }

        IEnumerable<SyntaxNode> methodBoundaries = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText.Equals(boundaryName, StringComparison.Ordinal))
            .Where(static method => method.TypeParameterList?.Parameters.Count == 1);
        IEnumerable<SyntaxNode> typeBoundaries = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText.Equals(boundaryName, StringComparison.Ordinal))
            .Where(static type => type.TypeParameterList?.Parameters.Count == 1);

        return methodBoundaries
            .Concat(typeBoundaries)
            .SelectMany(static boundary => boundary.DescendantNodes().OfType<GenericNameSyntax>())
            .Count(static generic => generic.Identifier.ValueText.Equals("CreateDefault", StringComparison.Ordinal)
                && generic.TypeArgumentList.Arguments.Count == 1
                && generic.TypeArgumentList.Arguments[0].ToString().Equals("T", StringComparison.Ordinal)) >= 3;
    }
}

internal static class NativeAotCompiledModelGuard
{
    private const string EntityTypeSuffix = "EntityType.cs";

    private const string UnsafeAccessorSuffix = "UnsafeAccessors.cs";

    public static IReadOnlyList<string> FindViolations(
        IReadOnlyDictionary<string, string> generatedFiles,
        IReadOnlyCollection<string>? mappedEntities = null)
    {
        string[] entities = generatedFiles.Keys
            .Where(static name => name.EndsWith(EntityTypeSuffix, StringComparison.Ordinal))
            .Select(static name => name[..^EntityTypeSuffix.Length])
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expectedEntities = mappedEntities is null
            ? entities
            : mappedEntities
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
        string[] accessorEntities = generatedFiles.Keys
            .Where(static name => name.EndsWith(UnsafeAccessorSuffix, StringComparison.Ordinal))
            .Select(static name => name[..^UnsafeAccessorSuffix.Length])
            .Order(StringComparer.Ordinal)
            .ToArray();
        List<string> violations = [];

        if (entities.Length == 0)
        {
            violations.Add("Generated contains no mapped *EntityType.cs files.");
        }

        foreach (string missing in expectedEntities.Except(entities, StringComparer.Ordinal))
        {
            violations.Add(
                $"{missing + EntityTypeSuffix} is missing for mapped entity {missing}.");
        }

        if (mappedEntities is not null)
        {
            foreach (string stale in entities.Except(expectedEntities, StringComparer.Ordinal))
            {
                violations.Add(
                    $"{stale + EntityTypeSuffix} has no matching mapped entity in ArcanumDbContext.");
            }
        }

        foreach (string entity in entities)
        {
            string entityFile = entity + EntityTypeSuffix;
            string accessorFile = entity + UnsafeAccessorSuffix;
            string accessorType = entity + "UnsafeAccessors";

            if (!generatedFiles.TryGetValue(accessorFile, out string? accessorSource))
            {
                violations.Add($"{accessorFile} is missing for {entityFile}.");
                continue;
            }

            if (!HasUnsafeAccessorAttribute(accessorSource))
            {
                violations.Add($"{accessorFile} contains no generated UnsafeAccessor member.");
            }

            if (!UsesAccessorType(generatedFiles[entityFile], accessorType))
            {
                violations.Add($"{entityFile} does not use {accessorType}.");
            }
        }

        foreach (string orphan in accessorEntities.Except(entities, StringComparer.Ordinal))
        {
            violations.Add($"{orphan + UnsafeAccessorSuffix} has no matching {orphan + EntityTypeSuffix}.");
        }

        return violations;
    }

    private static bool HasUnsafeAccessorAttribute(string source) =>
        CSharpSyntaxTree.ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<AttributeSyntax>()
            .Any(static attribute => attribute.Name.ToString() is "UnsafeAccessor" or "UnsafeAccessorAttribute"
                || attribute.Name.ToString().EndsWith(".UnsafeAccessor", StringComparison.Ordinal)
                || attribute.Name.ToString().EndsWith(".UnsafeAccessorAttribute", StringComparison.Ordinal));

    private static bool UsesAccessorType(string source, string accessorType) =>
        CSharpSyntaxTree.ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Any(access => access.Expression.ToString().Equals(accessorType, StringComparison.Ordinal)
                || access.Expression.ToString().EndsWith('.' + accessorType, StringComparison.Ordinal));
}
