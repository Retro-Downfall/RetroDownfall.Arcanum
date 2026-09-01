using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// The server-side <c>TheForge</c> namespaces are retired. Nothing in the four server projects and
/// nothing in the canonical documentation may name one — including in text the compiler never reads.
/// </summary>
/// <remarks>
/// <para>A declaration is caught by the per-domain contract tests, and a stale <c>using</c> is caught
/// by the compiler. Neither sees a namespace spelled out inside a string, a comment, an XML doc, or an
/// XAML <c>xmlns</c>, and this epic found exactly that: two hand-written logger categories reading
/// <c>"RetroDownfall.Arcanum.Api.TheForge.ProviderTest"</c>, which survived the move and would have
/// gone on naming a namespace that no longer exists in every log line they wrote.</para>
/// <para>The desktop application keeps its name, so <c>RetroDownfall.TheForge.Core</c> and
/// <c>RetroDownfall.TheForge.Ux</c> are untouched by these patterns: what is retired is
/// <c>Arcanum.{Core,Api,Cli,Infrastructure}</c> owning a namespace called after a desktop product.</para>
/// </remarks>
public sealed class RetiredServerNamespaceTests
{

    private static readonly string[] RetiredNamespaces =
    [
        "RetroDownfall.Arcanum.Core.TheForge",

        "RetroDownfall.Arcanum.Api.TheForge",

        "RetroDownfall.Arcanum.Cli.Commands.TheForge",

        "RetroDownfall.Arcanum.Infrastructure.TheForge",
    ];

    /// <summary>Documents whose vocabulary is a contract; dated review snapshots are historical records.</summary>
    private static readonly string[] CanonicalDocuments =
    [
        "README.md",

        "docs/Arcanum.Engineering.md",

        "docs/Arcanum.DESIGN.md",

        "docs/Arcanum.API.md",

        "docs/Arcanum.Command.Reference.md",

        "docs/Arcanum.Design.Human.md",

        "docs/Arcanum.DEBUGGING.Human.md",

        "docs/Arcanum.CHAT-LOOP.md",

        "docs/Arcanum.OATH.md",

        "docs/ArcanumOATH.Human.md",

        "docs/Compendium.README.md",
    ];

    [Fact]
    public void No_production_source_names_a_retired_server_namespace()
    {

        string root = NativeSqlCipherTestPaths.RepositoryRoot();

        List<string> offenders = [];

        foreach (string path in Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(static path => path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".axaml", StringComparison.Ordinal))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal))
        {

            string text = File.ReadAllText(path);

            foreach (string retired in RetiredNamespaces)
            {

                if (text.Contains(retired, StringComparison.Ordinal))
                {

                    offenders.Add($"{Path.GetRelativePath(root, path)} names {retired}");

                }

            }

        }

        Assert.Empty(offenders);

    }

    [Fact]
    public void No_canonical_document_names_a_retired_server_namespace()
    {

        string root = NativeSqlCipherTestPaths.RepositoryRoot();

        List<string> offenders = [];

        foreach (string relative in CanonicalDocuments)
        {

            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"{relative} is missing; the inventory names a document that does not exist.");

            string text = File.ReadAllText(path);

            foreach (string retired in RetiredNamespaces)
            {

                if (text.Contains(retired, StringComparison.Ordinal))
                {

                    offenders.Add($"{relative} names {retired}");

                }

            }

            foreach (string folder in (string[])["Core/TheForge", "Api/TheForge", "Infrastructure/TheForge", "Commands/TheForge"])
            {

                if (text.Contains(folder, StringComparison.Ordinal))
                {

                    offenders.Add($"{relative} names the retired folder {folder}");

                }

            }

        }

        Assert.Empty(offenders);

    }

}
