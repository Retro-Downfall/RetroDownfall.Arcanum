using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Collections;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// Who constructs the mirrored-credential helper, read from the compiled production source rather than from its spelling. The helper
/// deletes whichever account it was constructed with, so the set of things that construct it is the set of accounts it can ever remove,
/// and the credential-deletion inventory lists it as a decider only because that set is pinned.
/// </summary>
/// <remarks>
/// <c>BackupRestoreJournalKeyLeaseCallSiteTests</c> pins the same set by searching for the text <c>new MirroredOsCredential(</c>. A
/// construction written with a target-typed <c>new(...)</c> never contains that text, so it would add a constructor the text search
/// cannot see. This class asks the compilation instead: an explicit and a target-typed construction are each an object-creation
/// operation of the helper's type, whichever file holds them and however it is spelled, and the pin holds the helper to the same three
/// stores. The pin is a function over any set of compilations and is run on a synthetic source that breaks it in each way it names.
/// </remarks>
[Collection(HostedProducerAnalysisCollection.Name)]
[Trait("Category", "HostedProducerAnalysis")]
public sealed class MirroredCredentialConstructionTests : IClassFixture<SemanticModelScope>
{
    private const string Helper = "RetroDownfall.Arcanum.Infrastructure.Security.MirroredOsCredential";

    private static readonly string[] Stores =
    [
        "src/RetroDownfall.Arcanum.Infrastructure/Security/OsKeychainSecretStore.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Security/ProviderCredentialStore.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Security/WebResearchCredentialStore.cs",
    ];

    [Fact]
    public void The_mirrored_credential_helper_is_constructed_only_by_the_stores_with_fixed_accounts_however_the_construction_is_spelled()
    {
        Assert.Equal(Stores, Constructors([.. HostedGrimoireProducerInventory.ProductionCompilations], Helper));
    }

    [Fact]
    public void The_scan_finds_an_explicit_and_a_target_typed_construction_in_any_file_and_ignores_every_other_type()
    {
        const string Source = """
            namespace Fixture.Security;

            internal sealed class MirroredOsCredential(string account)
            {
                internal string Account { get; } = account;
            }

            internal sealed class OtherCredential(string account)
            {
                internal string Account { get; } = account;
            }
            """;

        const string Explicit = """
            namespace Fixture.Security;

            internal static class Explicit
            {
                internal static MirroredOsCredential Make() => new MirroredOsCredential("explicit");
            }
            """;

        const string TargetTyped = """
            namespace Fixture.Security;

            internal static class TargetTyped
            {
                private static readonly MirroredOsCredential Kept = new("field");

                internal static MirroredOsCredential Make() => new("return");

                internal static string Local()
                {
                    MirroredOsCredential local = new("local");

                    return local.Account;
                }
            }
            """;

        const string Unrelated = """
            namespace Fixture.Security;

            internal static class Unrelated
            {
                internal static OtherCredential Make() => new("other");

                internal static OtherCredential Typed() => new OtherCredential("other");
            }
            """;

        IReadOnlyList<Compilation> compilation = Synthetic(
            ("src/Fixture/MirroredOsCredential.cs", Source),
            ("src/Fixture/Explicit.cs", Explicit),
            ("src/Fixture/TargetTyped.cs", TargetTyped),
            ("src/Fixture/Unrelated.cs", Unrelated));

        Assert.Equal(
            ["src/Fixture/Explicit.cs", "src/Fixture/TargetTyped.cs"],
            Constructors(compilation, "Fixture.Security.MirroredOsCredential"));

        Assert.Empty(Constructors(Synthetic(("src/Fixture/MirroredOsCredential.cs", Source), ("src/Fixture/Unrelated.cs", Unrelated)), "Fixture.Security.MirroredOsCredential"));
    }

    /// <summary>The repository-relative files whose compilation constructs <paramref name="type"/>, by an explicit or a target-typed <c>new</c>.</summary>
    private static string[] Constructors(IReadOnlyList<Compilation> compilations, string type)
    {
        SortedSet<string> files = new(StringComparer.Ordinal);

        foreach (Compilation compilation in compilations)
        {
            foreach (SyntaxTree tree in compilation.SyntaxTrees)
            {
                SemanticModel model = SharedSemanticModels.Of(compilation, tree);

                foreach (SyntaxNode creation in tree.GetRoot().DescendantNodes().Where(static node => node is BaseObjectCreationExpressionSyntax))
                {
                    if (model.GetOperation(creation) is IObjectCreationOperation { Type: { } created }
                        && string.Equals(AuthorityCarrierScanner.Qualified(created), type, StringComparison.Ordinal))
                    {
                        _ = files.Add(tree.FilePath);
                    }
                }
            }
        }

        return [.. files];
    }

    private static IReadOnlyList<Compilation> Synthetic(params (string Path, string Text)[] sources)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "SyntheticMirroredCredential",
            [.. sources.Select(static source => CSharpSyntaxTree.ParseText(source.Text, path: source.Path))],
            TrustedPlatformReferences.Shared,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return [compilation];
    }
}
