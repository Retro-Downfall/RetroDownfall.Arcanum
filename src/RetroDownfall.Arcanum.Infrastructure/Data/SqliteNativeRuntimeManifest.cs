using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// The provenance of the native library this process loaded, read from the manifest embedded at
/// build time.
/// </summary>
/// <remarks>
/// Closed on purpose: an unknown schema version or a missing runtime identifier is a hard failure
/// rather than a default, because every value here is something the validator later asserts against
/// the running library. A manifest that can be partially understood is a manifest that can silently
/// stop checking something.
/// </remarks>
internal sealed record SqliteNativeRuntimeManifest(
    string SqliteVersion,
    string CipherVersion,
    string CipherProvider,
    string CipherProviderVersion,
    string CipherStatus,
    IReadOnlySet<string> CompileOptions,
    string AssetSha256,
    string AssetFileName)
{

    private const string ResourceName =
        "RetroDownfall.Arcanum.NativeSqlCipher.native-source-manifest.json";

    /// <summary>
    /// Stable code for a manifest that cannot be trusted. Carries no manifest content, because the
    /// failure is reported through logs an operator may share.
    /// </summary>
    internal const string InvalidManifestErrorCode = "Grimoire.NativeRuntimeManifestInvalid";

    private static readonly Lazy<SqliteNativeRuntimeManifest> Cached =
        new(LoadCore, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static SqliteNativeRuntimeManifest Load() => Cached.Value;

    private static SqliteNativeRuntimeManifest LoadCore()
    {

        using Stream? stream = typeof(SqliteNativeRuntimeManifest).Assembly
            .GetManifestResourceStream(ResourceName);

        if (stream is null)
        {

            throw new InvalidOperationException(
                $"{InvalidManifestErrorCode}: the native source manifest was not embedded.");

        }

        using JsonDocument document = JsonDocument.Parse(stream);

        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("schemaVersion", out JsonElement schemaVersion)
            || schemaVersion.GetInt32() != 1)
        {

            throw new InvalidOperationException(
                $"{InvalidManifestErrorCode}: unsupported manifest schema version.");

        }

        string runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;

        JsonElement asset = FindAsset(root, runtimeIdentifier);

        JsonElement pragmas = root.GetProperty("runtimePragmas");

        HashSet<string> compileOptions = new(StringComparer.Ordinal);

        foreach (JsonElement option in root.GetProperty("compileOptions").EnumerateArray())
        {

            if (!compileOptions.Add(option.GetString()!))
            {

                throw new InvalidOperationException(
                    $"{InvalidManifestErrorCode}: duplicate compile option.");

            }

        }

        string assetFileName = asset.GetProperty("outputFileName").GetString()!;

        string assetSha256 = asset.GetProperty("sha256").GetString()
            ?? throw new InvalidOperationException(
                $"{InvalidManifestErrorCode}: the asset for this runtime identifier has no recorded hash.");

        return new SqliteNativeRuntimeManifest(
            root.GetProperty("sqliteVersion").GetString()!,
            pragmas.GetProperty("cipherVersion").GetString()!,
            pragmas.GetProperty("cipherProvider").GetString()!,
            pragmas.GetProperty("cipherProviderVersion").GetString()!,
            pragmas.GetProperty("cipherStatus").GetString()!,
            compileOptions,
            assetSha256,
            assetFileName);

    }

    /// <summary>
    /// The manifest describes every shipping RID; only the one being run is relevant, and its
    /// absence means this build was produced for a platform Arcanum does not ship.
    /// </summary>
    private static JsonElement FindAsset(JsonElement root, string runtimeIdentifier)
    {

        foreach (JsonElement candidate in root.GetProperty("assets").EnumerateArray())
        {

            if (string.Equals(
                    candidate.GetProperty("rid").GetString(),
                    runtimeIdentifier,
                    StringComparison.Ordinal)
                && string.Equals(
                    candidate.GetProperty("status").GetString(),
                    "verified",
                    StringComparison.Ordinal))
            {

                return candidate.Clone();

            }

        }

        throw new InvalidOperationException(
            $"{InvalidManifestErrorCode}: no verified native asset is recorded for this runtime identifier.");

    }

    /// <summary>
    /// Hashes the delivered library and compares it with the manifest.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the file next to the application matches the recorded hash. <c>false</c>
    /// when it is absent or differs — either way the runtime is not the one that was verified.
    /// </returns>
    internal bool TryVerifyDeliveredAsset(out string? observedSha256)
    {

        observedSha256 = null;

        string path = Path.Combine(AppContext.BaseDirectory, AssetFileName);

        if (!File.Exists(path))
        {

            return false;

        }

        using FileStream stream = File.OpenRead(path);

        observedSha256 = Convert.ToHexStringLower(SHA256.HashData(stream));

        return string.Equals(observedSha256, AssetSha256, StringComparison.Ordinal);

    }

}
