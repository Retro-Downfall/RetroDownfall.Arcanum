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
