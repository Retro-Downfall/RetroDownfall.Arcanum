using System.Text.Json;
using System.Xml.Linq;

namespace RetroDownfall.Arcanum.Tests.NativeSqlCipher;

/// <summary>
/// The supply-chain contract for the hermetic SQLCipher runtime. Everything Arcanum ships as a
/// native binary has to be traceable to a pinned upstream source, a recorded toolchain, and a
/// hash that is checked before the file is delivered; these tests fail the build rather than let
/// an unattributed library reach an operator's machine.
/// </summary>
public sealed class NativeSqlCipherAssetContractTests
{

    /// <summary>
    /// The complete top-level manifest shape. Closed on purpose: an unexpected property means the
    /// manifest was written against a different contract than the readers enforce.
    /// </summary>
    private static readonly string[] ManifestProperties =
    [
        "schemaVersion",

        "sqlcipher",

        "sqliteVersion",

        "openssl",

        "compileOptions",

        "compatibilityDefaults",

        "runtimePragmas",

        "toolchains",

        "patches",

        "licenses",

        "assets",

        "sboms",
    ];

    /// <summary>
    /// Compile definitions the runtime validator later re-reads from
    /// <c>PRAGMA compile_options</c>. Losing any one of these silently changes Covenant's
    /// security or search behavior, so they are pinned in the manifest rather than left to the
    /// build script.
    /// </summary>
    private static readonly string[] RequiredCompileOptions =
    [
        "SQLCIPHER_CRYPTO_OPENSSL",

        "SQLITE_ENABLE_COLUMN_METADATA",

        "SQLITE_ENABLE_FTS5",

        "SQLITE_ENABLE_MATH_FUNCTIONS",

        "SQLITE_ENABLE_RTREE",

        "SQLITE_ENABLE_SNAPSHOT",

        "SQLITE_HAS_CODEC",

        "SQLITE_OMIT_LOAD_EXTENSION",

        "SQLITE_TEMP_STORE=2",

        "SQLITE_THREADSAFE=1",
    ];

    [Fact]
    public void Native_manifest_pins_approved_sources_and_shipping_rids()
    {

        JsonElement root = ReadManifest();

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());

        JsonElement sqlcipher = root.GetProperty("sqlcipher");

        Assert.Equal("v4.17.0", sqlcipher.GetProperty("tag").GetString());

        Assert.Equal(
            "f9788efa8ac4dfed75c03e4756b1666a1d0845da",
            sqlcipher.GetProperty("tagObject").GetString());

        Assert.Equal(
            "810db22f575ee7cf94ea96a3e91622b5fcece3dc",
            sqlcipher.GetProperty("commit").GetString());

        Assert.Equal("3.53.3", root.GetProperty("sqliteVersion").GetString());

        Assert.Equal("3.5.7", root.GetProperty("openssl").GetProperty("version").GetString());

        string[] expectedRids = [.. NativeSqlCipherTestPaths.ShippingRids];

        string[] actualRids =
        [
            .. root.GetProperty("assets")
                .EnumerateArray()
                .Select(static asset => asset.GetProperty("rid").GetString()!)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(expectedRids, actualRids);

    }

    [Fact]
    public void Native_manifest_has_exactly_the_closed_property_set()
    {

        JsonElement root = ReadManifest();

        string[] actual =
        [
            .. root.EnumerateObject()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal([.. ManifestProperties.Order(StringComparer.Ordinal)], actual);

    }

    [Fact]
    public void Native_manifest_records_verifiable_upstream_archives()
    {

        JsonElement root = ReadManifest();

        JsonElement sqlcipher = root.GetProperty("sqlcipher");

        AssertSha256(sqlcipher, "archiveSha256");

        Assert.Contains(
            sqlcipher.GetProperty("commit").GetString()!,
            sqlcipher.GetProperty("archiveUrl").GetString()!,
            StringComparison.Ordinal);

        JsonElement openssl = root.GetProperty("openssl");

        AssertSha256(openssl, "archiveSha256");

        AssertSha256(openssl, "publicKeysSha256");

        string fingerprint = openssl.GetProperty("signerFingerprint").GetString()!;

        Assert.Equal(40, fingerprint.Length);

        Assert.True(
            fingerprint.All(static character =>
                char.IsAsciiDigit(character) || char.IsAsciiLetterUpper(character)),
            $"OpenSSL signer fingerprint must be uppercase hexadecimal: {fingerprint}");

        Assert.EndsWith(".asc", openssl.GetProperty("signatureUrl").GetString()!, StringComparison.Ordinal);

    }

    [Fact]
    public void Native_manifest_pins_every_security_relevant_compile_option()
    {

        JsonElement root = ReadManifest();

        string[] options =
        [
            .. root.GetProperty("compileOptions")
                .EnumerateArray()
                .Select(static option => option.GetString()!),
        ];

        Assert.Equal(
            options.Length,
            options.Distinct(StringComparer.Ordinal).Count());

        foreach (string required in RequiredCompileOptions)
        {

            Assert.Contains(required, options, StringComparer.Ordinal);

        }

    }

    [Fact]
    public void Native_manifest_pins_the_exact_runtime_values_the_validator_enforces()
    {

        JsonElement pragmas = ReadManifest().GetProperty("runtimePragmas");

        Assert.Equal("3.53.3", pragmas.GetProperty("sqliteVersion").GetString());

        Assert.Equal("4.17.0 community", pragmas.GetProperty("cipherVersion").GetString());

        Assert.Equal("openssl", pragmas.GetProperty("cipherProvider").GetString());

        Assert.False(
            string.IsNullOrWhiteSpace(pragmas.GetProperty("cipherProviderVersion").GetString()));

        Assert.False(string.IsNullOrWhiteSpace(pragmas.GetProperty("cipherStatus").GetString()));

    }

    [Fact]
    public void Native_manifest_asset_records_are_well_formed_and_relative()
    {

        JsonElement root = ReadManifest();

        HashSet<string> seenRids = new(StringComparer.Ordinal);

        foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
        {

            string rid = asset.GetProperty("rid").GetString()!;

            Assert.True(seenRids.Add(rid), $"Duplicate asset record for RID {rid}.");

            Assert.Contains(rid, NativeSqlCipherTestPaths.ShippingRids);

            string path = asset.GetProperty("path").GetString()!;

            Assert.False(
                Path.IsPathRooted(path),
                $"Asset path for {rid} must be repository-relative: {path}");

            Assert.DoesNotContain("..", path, StringComparison.Ordinal);

            Assert.Equal('/', path[path.IndexOf('/', StringComparison.Ordinal)]);

            Assert.Equal(
                $"runtimes/{rid}/native/{NativeSqlCipherTestPaths.ExpectedOutputNames[rid]}",
                path);

            Assert.Equal(
                NativeSqlCipherTestPaths.ExpectedOutputNames[rid],
                asset.GetProperty("outputFileName").GetString());

            string status = asset.GetProperty("status").GetString()!;

            Assert.Contains(status, (string[])["verified", "pending"]);

            Assert.NotEmpty(asset.GetProperty("dynamicDependencies").EnumerateArray());

        }

    }

    /// <summary>
    /// A RID whose asset is recorded as verified must have a checked-in binary whose hash matches
    /// the manifest byte for byte. This is the assertion that makes the manifest evidence rather
    /// than documentation.
    /// </summary>
    [Fact]
    public void Every_verified_asset_matches_its_checked_in_binary()
    {

        List<string> failures = [];

        foreach (JsonElement asset in ReadManifest().GetProperty("assets").EnumerateArray())
        {

            string rid = asset.GetProperty("rid").GetString()!;

            if (asset.GetProperty("status").GetString() is not "verified")
            {

                continue;

            }

            AssertSha256(asset, "sha256");

            Assert.False(
                string.IsNullOrWhiteSpace(asset.GetProperty("compiler").GetString()),
                $"A verified asset records its compiler identity: {rid}");

            Assert.False(
                string.IsNullOrWhiteSpace(asset.GetProperty("linker").GetString()),
                $"A verified asset records its linker identity: {rid}");

            Assert.False(
                string.IsNullOrWhiteSpace(asset.GetProperty("image").GetString()),
                $"A verified asset records its build image or runner identity: {rid}");

            string file = NativeSqlCipherTestPaths.AssetPath(rid);

            if (!File.Exists(file))
            {

                failures.Add($"{rid}: manifest says verified but {file} is absent.");

                continue;

            }

            string actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(file)));

            if (!string.Equals(actual, asset.GetProperty("sha256").GetString(), StringComparison.Ordinal))
            {

                failures.Add(
                    $"{rid}: checked-in binary hashes {actual}, manifest records "
                    + $"{asset.GetProperty("sha256").GetString()}.");

            }

        }

        Assert.True(failures.Count == 0, string.Join(global::System.Environment.NewLine, failures));

    }

    /// <summary>
    /// A RID still waiting on its native runner must not have a binary in the tree: an unverified
    /// file next to a pending record is exactly the substitution the manifest exists to prevent.
    /// </summary>
    [Fact]
    public void Every_pending_asset_has_no_checked_in_binary()
    {

        List<string> failures = [];

        foreach (JsonElement asset in ReadManifest().GetProperty("assets").EnumerateArray())
        {

            string rid = asset.GetProperty("rid").GetString()!;

            if (asset.GetProperty("status").GetString() is not "pending")
            {

                continue;

            }

            Assert.Null(asset.GetProperty("sha256").GetString());

            if (File.Exists(NativeSqlCipherTestPaths.AssetPath(rid)))
            {

                failures.Add(
                    $"{rid}: a native binary is checked in but the manifest records it as pending.");

            }

        }

        Assert.True(failures.Count == 0, string.Join(global::System.Environment.NewLine, failures));

    }

    [Fact]
    public void Native_manifest_hashes_every_license_and_sbom()
    {

        JsonElement root = ReadManifest();

        foreach (string collection in (string[])["licenses", "sboms"])
        {

            JsonElement records = root.GetProperty(collection);

            Assert.NotEmpty(records.EnumerateArray());

            foreach (JsonElement record in records.EnumerateArray())
            {

                AssertSha256(record, "sha256");

                string relative = record.GetProperty("path").GetString()!;

                Assert.False(Path.IsPathRooted(relative), $"{collection} path must be relative.");

                string file = Path.Combine(
                    NativeSqlCipherTestPaths.AssetProject,
                    relative.Replace('/', Path.DirectorySeparatorChar));

                Assert.True(File.Exists(file), $"Missing {collection} file: {file}");

                Assert.Equal(
                    record.GetProperty("sha256").GetString(),
                    Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                        File.ReadAllBytes(file))));

            }

        }

    }

    [Fact]
    public void Native_project_is_the_only_declared_asset_source()
    {

        string[] projects = Directory.GetFiles(
            Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), "src"),
            "*.csproj",
            SearchOption.AllDirectories);

        List<string> offenders = [];

        foreach (string project in projects)
        {

            if (string.Equals(
                    Path.GetFileName(project),
                    "RetroDownfall.Arcanum.NativeSqlCipher.csproj",
                    StringComparison.Ordinal))
            {

                continue;

            }

            if (XDocument.Load(project)
                .Descendants("PackageReference")
                .Any(static item => item.Attribute("Include")?.Value.StartsWith(
                    "SQLitePCLRaw.bundle",
                    StringComparison.Ordinal) == true))
            {

                offenders.Add(Path.GetFileName(project));

            }

        }

        Assert.True(
            offenders.Count == 0,
            "A bundled SQLite provider package delivers native libraries Arcanum does not build "
            + "or verify. Reference RetroDownfall.Arcanum.NativeSqlCipher instead: "
            + string.Join(", ", offenders));

    }

    [Fact]
    public void Infrastructure_removes_bundle_and_pins_provider()
    {

        XDocument project = XDocument.Load(NativeSqlCipherTestPaths.InfrastructureProject);

        Assert.DoesNotContain(
            project.Descendants("PackageReference"),
            static item => string.Equals(
                item.Attribute("Include")?.Value,
                "SQLitePCLRaw.bundle_e_sqlcipher",
                StringComparison.Ordinal));

        XElement provider = Assert.Single(
            project.Descendants("PackageReference"),
            static item => string.Equals(
                item.Attribute("Include")?.Value,
                "SQLitePCLRaw.provider.e_sqlcipher",
                StringComparison.Ordinal));

        Assert.False(string.IsNullOrWhiteSpace(provider.Attribute("Version")?.Value));

    }

    /// <summary>
    /// Reads the manifest, failing with the path rather than a JSON exception when it is absent.
    /// </summary>
    private static JsonElement ReadManifest()
    {

        string path = NativeSqlCipherTestPaths.Manifest;

        Assert.True(File.Exists(path), $"Missing native source manifest: {path}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement.Clone();

    }

    /// <summary>
    /// Requires a lowercase 64-character SHA-256 digest. Uppercase and truncated digests are
    /// rejected so a hand-edited manifest cannot compare unequal to a correctly computed hash.
    /// </summary>
    private static void AssertSha256(JsonElement owner, string property)
    {

        string? value = owner.GetProperty(property).GetString();

        Assert.False(string.IsNullOrWhiteSpace(value), $"Missing digest: {property}");

        Assert.Equal(64, value!.Length);

        Assert.True(
            value.All(static character =>
                char.IsAsciiDigit(character) || (character is >= 'a' and <= 'f')),
            $"Digest {property} must be lowercase hexadecimal: {value}");

    }

}
