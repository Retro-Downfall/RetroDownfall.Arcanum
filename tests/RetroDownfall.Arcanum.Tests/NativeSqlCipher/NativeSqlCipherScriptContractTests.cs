namespace RetroDownfall.Arcanum.Tests.NativeSqlCipher;

/// <summary>
/// The build and verification scripts are the only things standing between an upstream tarball and
/// a library Arcanum loads into every process. These tests hold them to the properties that make
/// the resulting binary trustworthy: nothing is used before its hash is checked, nothing is taken
/// from the build machine's environment, and no runtime identifier can be quietly skipped.
/// </summary>
public sealed class NativeSqlCipherScriptContractTests
{

    /// <summary>
    /// Ambient crypto sources. If any of these reach the build, the shipped library depends on
    /// whatever the build machine happened to have installed.
    /// </summary>
    private static readonly string[] AmbientOpenSslSources =
    [
        "brew --prefix openssl",

        "/usr/lib/libssl",

        "/usr/local/opt/openssl",

        "/opt/homebrew/opt/openssl",

        "pkg-config --cflags openssl",
    ];

    [Fact]
    public void Native_build_script_refuses_ambient_openssl()
    {

        string script = BuildScript();

        foreach (string ambient in AmbientOpenSslSources)
        {

            Assert.DoesNotContain(ambient, script, StringComparison.Ordinal);

        }

    }

    [Fact]
    public void Native_build_script_takes_every_source_and_flag_from_the_manifest()
    {

        string script = BuildScript();

        Assert.Contains("read_manifest_value", script, StringComparison.Ordinal);

        Assert.Contains("SOURCE_DATE_EPOCH", script, StringComparison.Ordinal);

        // Compile definitions are read from the manifest rather than repeated here, so the manifest
        // stays the single place a compile option can be added, removed, or audited.
        Assert.Contains(
            "read_manifest_value '.compileOptions[]'",
            script,
            StringComparison.Ordinal);

        Assert.DoesNotContain("-DSQLITE_OMIT_LOAD_EXTENSION", script, StringComparison.Ordinal);

    }

    [Fact]
    public void Native_build_script_verifies_every_download_before_using_it()
    {

        string script = BuildScript();

        Assert.Contains("verify_sha256 \"${WORK_DIR}/sqlcipher.tar.gz\"", script, StringComparison.Ordinal);

        Assert.Contains("verify_sha256 \"${WORK_DIR}/openssl.tar.gz\"", script, StringComparison.Ordinal);

        Assert.Contains("verify_sha256 \"${WORK_DIR}/pubkeys.asc\"", script, StringComparison.Ordinal);

        // A good signature is not enough: it has to be the pinned signer.
        Assert.Contains("gpg", script, StringComparison.Ordinal);

        Assert.Contains("VALIDSIG ${OPENSSL_FINGERPRINT}", script, StringComparison.Ordinal);

        int firstExtract = script.IndexOf("tar xzf", StringComparison.Ordinal);

        int lastVerify = script.LastIndexOf("verify_sha256 \"${WORK_DIR}", StringComparison.Ordinal);

        Assert.True(
            lastVerify < firstExtract,
            "Every archive hash must be verified before anything is extracted.");

    }

    [Fact]
    public void Native_build_script_proves_the_pinned_tag_resolves_to_the_pinned_commit()
    {

        string script = BuildScript();

        Assert.Contains("git ls-remote", script, StringComparison.Ordinal);

        Assert.Contains("REMOTE_TAG_OBJECT", script, StringComparison.Ordinal);

        Assert.Contains("REMOTE_COMMIT", script, StringComparison.Ordinal);

    }

    [Fact]
    public void Native_scripts_fail_fast_and_never_eval()
    {

        foreach (string script in (string[])[BuildScript(), VerifyScript()])
        {

            Assert.Contains("set -euo pipefail", script, StringComparison.Ordinal);

            Assert.DoesNotContain("eval ", script, StringComparison.Ordinal);

        }

    }

    [Fact]
    public void Native_verifier_has_manifest_only_rid_and_all_modes()
    {

        string script = VerifyScript();

        Assert.Contains("--manifest-only", script, StringComparison.Ordinal);

        Assert.Contains("--rid", script, StringComparison.Ordinal);

        Assert.Contains("--all", script, StringComparison.Ordinal);

    }

    /// <summary>
    /// A RID the host cannot rebuild has to fail, not skip. A skipped RID reported as a pass is
    /// indistinguishable from a verified one in a CI summary, which is the failure mode that lets
    /// an unverified binary ship.
    /// </summary>
    [Fact]
    public void Native_verifier_fails_rather_than_skipping_a_rid_it_cannot_prove()
    {

        string script = VerifyScript();

        Assert.Contains("cannot rebuild the RID", script, StringComparison.Ordinal);

        Assert.Contains("status: pending", script, StringComparison.Ordinal);

        Assert.DoesNotContain("SKIP", script, StringComparison.Ordinal);

    }

    [Fact]
    public void Native_verifier_checks_exported_symbols_and_dynamic_dependencies()
    {

        string script = VerifyScript();

        Assert.Contains("verify_rid_symbols", script, StringComparison.Ordinal);

        Assert.Contains("verify_rid_dependencies", script, StringComparison.Ordinal);

        Assert.Contains("sqlite3_load_extension", script, StringComparison.Ordinal);

    }

    [Fact]
    public void Native_scripts_are_executable()
    {

        foreach (string path in (string[])
                 [
                     NativeSqlCipherTestPaths.BuildScript,

                     NativeSqlCipherTestPaths.VerifyScript,
                 ])
        {

            Assert.True(File.Exists(path), $"Missing script: {path}");

            if (OperatingSystem.IsWindows())
            {

                continue;

            }

            UnixFileMode mode = File.GetUnixFileMode(path);

            Assert.True(
                mode.HasFlag(UnixFileMode.UserExecute),
                $"Script is not executable: {path}");

        }

    }

    private static string BuildScript() => ReadScript(NativeSqlCipherTestPaths.BuildScript);

    private static string VerifyScript() => ReadScript(NativeSqlCipherTestPaths.VerifyScript);

    private static string ReadScript(string path)
    {

        Assert.True(File.Exists(path), $"Missing script: {path}");

        return File.ReadAllText(path);

    }

}
