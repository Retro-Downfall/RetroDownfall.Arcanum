using System.Security.Cryptography;
using System.Text.Json;

namespace RetroDownfall.Arcanum.Tests.NativeSqlCipher;

/// <summary>
/// What actually reached the output directory this test is running from.
/// </summary>
/// <remarks>
/// The manifest and the verify script check the checked-in asset; this checks the delivery. They are
/// different failures: an MSBuild change can stop copying the library, copy the wrong RID's, or —
/// worst — leave a second SQLite library alongside it, and the loader would then pick by search
/// order rather than by the delivery contract.
/// </remarks>
public sealed class NativeSqlCipherDeliveryTests
{

    [Fact]
    public void Output_contains_exactly_one_manifest_matching_native_asset()
    {

        string[] delivered =
        [
            .. Directory.GetFiles(AppContext.BaseDirectory)
                .Where(static file =>
                    Path.GetFileName(file).Contains("e_sqlcipher", StringComparison.OrdinalIgnoreCase)
                    && Path.GetExtension(file) is ".dylib" or ".so" or ".dll"
                    && !Path.GetFileName(file).StartsWith("SQLitePCLRaw.", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
        ];

        string single = Assert.Single(delivered);

        Assert.Equal(ExpectedFileName(), Path.GetFileName(single));

        Assert.Equal(
            ExpectedSha256(),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(single))));

    }

    /// <summary>
    /// The delivery must not also drop a per-RID <c>runtimes/</c> tree; two copies mean the loader
    /// chooses, and which one it chooses is not something the manifest can attest to.
    /// </summary>
    [Fact]
    public void Output_has_no_second_copy_under_a_runtimes_tree()
    {

        string runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");

        if (!Directory.Exists(runtimes))
        {

            return;

        }

        string[] duplicates =
        [
            .. Directory.GetFiles(runtimes, "*e_sqlcipher*", SearchOption.AllDirectories)
                .Where(static file => Path.GetExtension(file) is ".dylib" or ".so" or ".dll")
                .Select(file => Path.GetRelativePath(AppContext.BaseDirectory, file))
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            duplicates.Length == 0,
            "A second SQLCipher library was delivered under runtimes/: "
            + string.Join(", ", duplicates));

    }

    private static string ExpectedFileName() =>
        NativeSqlCipherTestPaths.ExpectedOutputNames[CurrentRid()];

    private static string ExpectedSha256()
    {

        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(NativeSqlCipherTestPaths.Manifest));

        foreach (JsonElement asset in document.RootElement.GetProperty("assets").EnumerateArray())
        {

            if (string.Equals(asset.GetProperty("rid").GetString(), CurrentRid(), StringComparison.Ordinal))
            {

                return asset.GetProperty("sha256").GetString()
                    ?? throw new InvalidOperationException(
                        $"The manifest records no hash for {CurrentRid()}, so this build should not "
                        + "have produced a delivery at all.");

            }

        }

        throw new InvalidOperationException($"The manifest declares no asset for {CurrentRid()}.");

    }

    private static string CurrentRid() =>
        System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;

}
