using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// Structural rules for the CLI project that no behavioural test would notice breaking.
/// </summary>
public sealed class CliArchitectureTests
{
    private const string CliProject = "src/RetroDownfall.Arcanum.Cli";

    private static readonly HashSet<string> RegistrationMethods = new(StringComparer.Ordinal)
    {
        "AddSingleton",
        "AddTransient",
        "AddScoped",
        "TryAddSingleton",
        "TryAddTransient",
        "TryAddScoped",
    };

    /// <summary>
    /// A service registered for the UX layer must be asked for by something that ships. A registration
    /// whose only reader is a test keeps dead production code alive: it compiles, it is covered, and it
    /// is never executed (the Markdown renderer sat registered and unused while its tests passed).
    /// </summary>
    [Fact]
    public void Every_registered_cli_ux_service_has_a_production_consumer()
    {
        CliSource[] sources = LoadCliSources();

        Dictionary<string, string> declaredIn = new(StringComparer.Ordinal);

        foreach (CliSource source in sources)
        {
            foreach (BaseTypeDeclarationSyntax declaration in source.Root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                declaredIn.TryAdd(declaration.Identifier.Text, source.RelativePath);
            }
        }

        List<string> uxServices = [];

        foreach (CliSource source in sources)
        {
            foreach (InvocationExpressionSyntax invocation in source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax generic }
                    || !RegistrationMethods.Contains(generic.Identifier.Text)
                    || generic.TypeArgumentList.Arguments.Count == 0)
                {
                    continue;
                }

                // The first type argument is the service; with two, the second is only its implementation.
                string service = SimpleName(generic.TypeArgumentList.Arguments[0]);

                if (declaredIn.TryGetValue(service, out string? path)
                    && path.Contains("/UX/", StringComparison.Ordinal))
                {
                    uxServices.Add(service);
                }
            }
        }

        Assert.NotEmpty(uxServices);

        string[] unconsumed = uxServices
            .Distinct(StringComparer.Ordinal)
            .Where(service => !HasProductionConsumer(sources, service))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unconsumed.Length == 0,
            "Registered CLI UX services nothing in src/ consumes (delete them, or wire a consumer):\n"
                + string.Join("\n", unconsumed));
    }

    /// <summary>
    /// The CLI reaches stored blobs only through the lifecycle contract in Core. The encryption verbs
    /// are the documented offline exception to API-first (Command Reference): they run the lifecycle
    /// in-process under the exclusive bootstrap, and nothing in the CLI may reach past that contract
    /// into the Infrastructure storage implementation.
    /// </summary>
    [Fact]
    public void Cli_never_references_infrastructure_storage()
    {
        string[] offenders = LoadCliSources()
            .Where(static source => source.Root.DescendantNodes()
                .OfType<NameSyntax>()
                .Any(static name => name is QualifiedNameSyntax
                    && name.ToString().Contains("Infrastructure.Storage", StringComparison.Ordinal)))
            .Select(static source => source.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "CLI sources that reference RetroDownfall.Arcanum.Infrastructure.Storage:\n"
                + string.Join("\n", offenders));
    }

    /// <summary>
    /// The blob lifecycle is resolved in-process by the encryption verbs alone, which is the exact
    /// extent of the documented offline exception.
    /// </summary>
    [Fact]
    public void Only_the_encryption_verbs_resolve_the_blob_lifecycle_in_process()
    {
        string[] resolvers = LoadCliSources()
            .Where(static source => source.Root.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Any(static name => name.Identifier.Text == "IBlobEncryptionLifecycleService"))
            .Select(static source => source.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["src/RetroDownfall.Arcanum.Cli/Commands/DataEncryptionCommands.cs"], resolvers);
    }

    private static bool HasProductionConsumer(CliSource[] sources, string service)
    {
        foreach (CliSource source in sources)
        {
            foreach (IdentifierNameSyntax name in source.Root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (name.Identifier.Text != service
                    || IsInsideDeclarationOf(name, service)
                    || IsRegistrationTypeArgument(name))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private static bool IsInsideDeclarationOf(SyntaxNode node, string typeName) =>
        node.Ancestors().OfType<BaseTypeDeclarationSyntax>().Any(declaration => declaration.Identifier.Text == typeName);

    private static bool IsRegistrationTypeArgument(IdentifierNameSyntax name) =>
        name.Ancestors().OfType<TypeArgumentListSyntax>().FirstOrDefault() is { Parent: GenericNameSyntax generic }
        && RegistrationMethods.Contains(generic.Identifier.Text);

    private static string SimpleName(TypeSyntax type) =>
        type switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            _ => type.ToString(),
        };

    private static CliSource[] LoadCliSources()
    {
        string repositoryRoot = TestRepositoryPaths.RepositoryRoot();
        string cliRoot = Path.Combine(repositoryRoot, CliProject);
        string separator = Path.DirectorySeparatorChar.ToString();

        return
        [
            .. Directory.EnumerateFiles(cliRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                    && !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
                .Select(path => new CliSource(
                    Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'),
                    CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot())),
        ];
    }

    private sealed record CliSource(string RelativePath, SyntaxNode Root);
}
