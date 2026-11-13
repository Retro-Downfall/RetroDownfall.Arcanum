using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Generated;

namespace RetroDownfall.Arcanum.Tests.Build;

public sealed class NativeAotEnumMetadataBoundaryTests
{
    [Fact]
    public void Only_empty_concrete_enum_constructors_contain_the_metadata_suppression()
    {
        Assert.Empty(FindViolations(ReadBoundary()));
    }

    [Theory]
    [InlineData("body")]
    [InlineData("hash")]
    [InlineData("enum")]
    [InlineData("attribute")]
    public void Guard_rejects_behavior_or_scope_changes_hidden_by_the_metadata_boundary(string mutation)
    {
        string source = ReadBoundary();

        source = mutation switch
        {
            "body" => source.Replace("    {\n    }", "    {\n        GC.KeepAlive(null);\n    }", StringComparison.Ordinal),
            "hash" => source.Replace("static value => (int)value", "static value => 0", StringComparison.Ordinal),
            "enum" => source.Replace("ValueComparer<MessageRole>", "ValueComparer<WorkspaceType>", StringComparison.Ordinal),
            "attribute" => source.Replace("UnconditionalSuppressMessage", "SuppressMessage", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        Assert.NotEmpty(FindViolations(source));
    }

    [Fact]
    public void Natural_compiled_model_keeps_only_the_two_concrete_mapping_and_property_comparers()
    {
        Microsoft.EntityFrameworkCore.Metadata.IProperty role = ArcanumDbContextModel.Instance
            .FindEntityType(typeof(Entry))!.FindProperty(nameof(Entry.Role))!;

        Microsoft.EntityFrameworkCore.Metadata.IProperty type = ArcanumDbContextModel.Instance
            .FindEntityType(typeof(Campaign))!.FindProperty(nameof(Campaign.Type))!;

        Assert.Same(MessageRoleValueComparer.Default, role.GetTypeMapping().Comparer);

        Assert.Same(MessageRoleValueComparer.Default, role.GetTypeMapping().KeyComparer);

        Assert.Same(MessageRoleValueComparer.Default, role.GetValueComparer());

        Assert.Same(WorkspaceTypeValueComparer.Default, type.GetTypeMapping().Comparer);

        Assert.Same(WorkspaceTypeValueComparer.Default, type.GetTypeMapping().KeyComparer);

        Assert.Same(WorkspaceTypeValueComparer.Default, type.GetValueComparer());
    }

    private static IReadOnlyList<string> FindViolations(string source)
    {
        CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        List<string> violations = [];

        (string Name, string Enum)[] expected =
        [
            ("MessageRoleValueComparer", "MessageRole"),
            ("WorkspaceTypeValueComparer", "WorkspaceType"),
        ];

        ClassDeclarationSyntax[] classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToArray();

        if (root.DescendantNodes().OfType<AttributeSyntax>().Count(static attribute => attribute.Name.ToString() == "UnconditionalSuppressMessage") != 2)
        {
            violations.Add("Only the two constructor suppressions are permitted.");
        }

        if (classes.Length != 2)
        {
            violations.Add("The metadata boundary must contain exactly two concrete comparers.");
        }

        foreach ((string name, string enumType) in expected)
        {
            ClassDeclarationSyntax? type = classes.SingleOrDefault(item => item.Identifier.ValueText == name);

            ConstructorDeclarationSyntax[] constructors = type?.Members.OfType<ConstructorDeclarationSyntax>().ToArray() ?? [];

            if (type?.TypeParameterList is not null || type?.BaseList?.Types.SingleOrDefault()?.Type.ToString() != $"ValueComparer<{enumType}>"
                || constructors.Length != 1)
            {
                violations.Add(name + " must close exactly its reviewed enum and one constructor.");

                continue;
            }

            ConstructorDeclarationSyntax constructor = constructors[0];

            if (!constructor.Modifiers.Any(SyntaxKind.PrivateKeyword) || constructor.ParameterList.Parameters.Count != 0
                || constructor.Body?.Statements.Count != 0 || constructor.ExpressionBody is not null
                || constructor.Initializer?.Kind() != SyntaxKind.BaseConstructorInitializer)
            {
                violations.Add(name + " constructor must remain private, parameterless and empty.");
            }

            string[] arguments = constructor.Initializer?.ArgumentList.Arguments
                .Select(static argument => argument.Expression.NormalizeWhitespace().ToFullString()).ToArray() ?? [];

            if (!arguments.SequenceEqual(new[]
                {
                    "static (left, right) => left == right",
                    "static value => (int)value",
                    "static value => value",
                }, StringComparer.Ordinal))
            {
                violations.Add(name + " base arguments must remain the reviewed scalar expressions.");
            }

            AttributeSyntax[] attributes = constructor.AttributeLists.SelectMany(static list => list.Attributes).ToArray();

            if (attributes.Length != 1 || attributes[0].Name.ToString() != "UnconditionalSuppressMessage"
                || attributes[0].ArgumentList?.Arguments.Count != 3
                || attributes[0].ArgumentList!.Arguments[0].Expression.ToString() != "\"AOT\""
                || attributes[0].ArgumentList!.Arguments[1].Expression.ToString() != "\"IL3050:System.Enum.GetValues(System.Type)\""
                || attributes[0].ArgumentList!.Arguments[2].NameEquals?.Name.Identifier.ValueText != "Justification")
            {
                violations.Add(name + " must scope the one documented diagnostic to its constructor.");
            }
        }

        return violations;
    }

    private static string ReadBoundary([CallerFilePath] string path = "")
    {
        DirectoryInfo directory = new(Path.GetDirectoryName(path)!);

        while (!File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
        {
            directory = directory.Parent ?? throw new InvalidOperationException("Repository root absent.");
        }

        return File.ReadAllText(Path.Combine(directory.FullName,
            "src/RetroDownfall.Arcanum.Infrastructure/Data/NativeAotEnumValueComparers.cs"));
    }
}
