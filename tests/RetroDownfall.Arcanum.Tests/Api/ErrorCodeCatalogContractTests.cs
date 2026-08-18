using System.Reflection;
using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// API reference §8.23 names <c>ErrorCodes</c> (Core) as the home of every wire-stable dotted code
/// and the §8.23 table as the catalog clients switch on. An endpoint that builds
/// <c>new Error("Some.Code", …)</c> from an inline literal bypasses both, so a code can ship on the
/// wire without ever reaching the constant table or the published catalog. These contracts hold the
/// three in place.
/// </summary>
public sealed class ErrorCodeCatalogContractTests
{

    private static readonly Regex InlineErrorCode = new(
        "new\\s+Error\\(\\s*\"([^\"]+)\"",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Any two-segment PascalCase string literal, which is the shape every wire-stable code has.
    /// Only literals whose first segment names a real <c>ErrorCodes</c> nest are treated as codes,
    /// so ordinary prose such as <c>"HttpContext.RequestAborted"</c> in a comment is not an offender.
    /// </summary>
    private static readonly Regex DottedCodeLiteral = new(
        "\"([A-Z][A-Za-z0-9]*)\\.([A-Z][A-Za-z0-9]*)\"",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Api_never_constructs_a_wire_error_code_from_an_inline_literal()
    {

        List<string> offenders = [];

        string apiRoot = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Api");

        foreach (string file in Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories))
        {

            if (IsBuildOutput(apiRoot, file))
            {

                continue;

            }

            string source = File.ReadAllText(file);

            foreach (Match match in InlineErrorCode.Matches(source))
            {

                offenders.Add(
                    $"{Path.GetRelativePath(apiRoot, file)}: \"{match.Groups[1].Value}\"");

            }

        }

        Assert.True(
            offenders.Count == 0,
            "Wire-stable error codes must come from ErrorCodes (Core) so the constant table and the "
            + "§8.23 catalog stay authoritative; these Api sites inline the literal instead: "
            + string.Join("; ", offenders));

    }

    /// <summary>
    /// The inline-literal contract above only sees <c>new Error("…")</c>. Endpoints that hand the code
    /// to a local envelope helper — or park it in a file-local <c>static readonly Error</c> — sail past
    /// it, which is how <c>Operation.NotFound</c>, <c>Perception.PathNotAllowed</c> and
    /// <c>Codex.PathNotContained</c> reached the wire without ever reaching <c>ErrorCodes</c>.
    /// </summary>
    [Fact]
    public void Api_never_names_a_code_in_a_declared_error_family_by_string_literal()
    {

        HashSet<string> families = DeclaredErrorCodes()
            .Select(declared => declared.Nest)
            .ToHashSet(StringComparer.Ordinal);

        List<string> offenders = [];

        foreach ((string relativePath, string source) in EnumerateApiSources())
        {

            foreach (Match match in DottedCodeLiteral.Matches(source))
            {

                if (families.Contains(match.Groups[1].Value))
                {

                    offenders.Add($"{relativePath}: \"{match.Groups[1].Value}.{match.Groups[2].Value}\"");

                }

            }

        }

        Assert.True(
            offenders.Count == 0,
            "A code inside a declared ErrorCodes family must be referenced through its constant, not "
            + "spelled out, so the constant table and the §8.23 catalog stay authoritative; these Api "
            + "sites spell it out instead: " + string.Join("; ", offenders));

    }

    /// <summary>
    /// Codes clients must switch on but which §8.23 never listed. Each one is emitted by a live route,
    /// so a client written against the published catalog had no row to match.
    /// </summary>
    [Theory]
    [InlineData("Operation.NotFound")]
    [InlineData("Operation.StateConflict")]
    [InlineData("Operation.InvalidState")]
    [InlineData("Perception.PathNotAllowed")]
    [InlineData("Codex.PathNotContained")]
    [InlineData("Spell.WriteFailed")]
    [InlineData("Validation.InvalidLore")]
    [InlineData("Validation.InvalidKey")]
    [InlineData("Execution.NotFound")]
    public void Catalog_and_constant_table_both_carry_every_code_a_route_emits(string code)
    {

        Assert.Contains(code, DeclaredErrorCodes().Select(declared => declared.Value));

        Assert.Contains(code, ReadErrorCatalogSection(), StringComparison.Ordinal);

    }

    [Fact]
    public void Every_declared_error_code_value_matches_its_nest_and_member_name()
    {

        List<string> offenders = [];

        foreach ((string nest, string member, string value) in DeclaredErrorCodes())
        {

            string expected = $"{nest}.{member}";

            if (!string.Equals(value, expected, StringComparison.Ordinal))
            {

                offenders.Add($"ErrorCodes.{expected} = \"{value}\"");

            }

        }

        Assert.True(
            offenders.Count == 0,
            "Every ErrorCodes constant's value is its own dotted path: " + string.Join("; ", offenders));

    }

    /// <summary>
    /// The host rejects an unauthenticated request with <c>Auth.Unauthorized</c>; §8.23 documented
    /// only the client-side <c>Security.MissingApiKey</c> on its 401 row, so a client switching on
    /// the published catalog never matched a real 401 body.
    /// </summary>
    [Fact]
    public void Catalog_documents_the_401_code_the_host_actually_emits()
    {

        string catalog = ReadErrorCatalogSection();

        string[] unauthorizedRows = catalog
            .Split('\n')
            .Where(line => line.Contains("| 401 |", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(unauthorizedRows);

        Assert.Contains(
            unauthorizedRows,
            row => row.Contains(ErrorCodes.Auth.Unauthorized, StringComparison.Ordinal));

    }

    private static IEnumerable<(string RelativePath, string Source)> EnumerateApiSources()
    {

        string apiRoot = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Api");

        foreach (string file in Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories))
        {

            if (IsBuildOutput(apiRoot, file))
            {

                continue;

            }

            yield return (Path.GetRelativePath(apiRoot, file), File.ReadAllText(file));

        }

    }

    private static IEnumerable<(string Nest, string Member, string Value)> DeclaredErrorCodes()
    {

        foreach (Type nest in typeof(ErrorCodes).GetNestedTypes(BindingFlags.Public))
        {

            FieldInfo[] fields = nest.GetFields(BindingFlags.Public | BindingFlags.Static);

            foreach (FieldInfo field in fields)
            {

                if (field is { IsLiteral: true, IsInitOnly: false }
                    && field.GetRawConstantValue() is string value)
                {

                    yield return (nest.Name, field.Name, value);

                }

            }

        }

    }

    private static string ReadErrorCatalogSection()
    {

        string path = Path.Combine(FindRepositoryRoot(), "docs", "Arcanum.API.md");

        string document = File.ReadAllText(path);

        int start = document.IndexOf("### 8.23", StringComparison.Ordinal);

        Assert.True(start >= 0, "docs/Arcanum.API.md no longer contains the §8.23 error catalog.");

        int end = document.IndexOf("### 8.24", start, StringComparison.Ordinal);

        return end < 0 ? document[start..] : document[start..end];

    }

    private static bool IsBuildOutput(string root, string file)
    {

        string relative = Path.GetRelativePath(root, file);

        string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.Ordinal)
            || string.Equals(segment, "obj", StringComparison.Ordinal));

    }

    private static string FindRepositoryRoot()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

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
