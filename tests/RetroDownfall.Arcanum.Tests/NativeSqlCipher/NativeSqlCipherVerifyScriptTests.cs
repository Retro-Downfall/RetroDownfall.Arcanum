using System.Text;
using System.Text.Json;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.NativeSqlCipher;

/// <summary>
/// Drives <c>scripts/verify-native-sqlcipher.sh --rid win-x64</c> against the checked-in Windows
/// asset with a stubbed <c>dumpbin</c>. The script runs on any host; the Windows job in
/// <c>verify-native-sqlcipher.yml</c> is where the real <c>dumpbin</c> exists, so these tests pin the
/// comparison logic that job relies on.
/// </summary>
public sealed class NativeSqlCipherVerifyScriptTests : IDisposable
{
    private const string Rid = "win-x64";

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "arcanum-verify-native-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [SkippableFact]
    public async Task A_win_asset_whose_exports_and_imports_match_the_manifest_passes()
    {
        RequireScriptHost();

        ScriptResult result = await RunAsync(withDumpbin: true, exports: SqliteExports(), dependents: ManifestDependents());

        Assert.True(result.ExitCode == 0, result.Output);

        Assert.Contains($"{Rid} exports no symbol outside the SQLite C API", result.Output, StringComparison.Ordinal);

        Assert.Contains($"{Rid} links only its declared dynamic dependencies", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("UNVERIFIED", result.Output, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_win_asset_that_exports_a_non_sqlite_symbol_fails()
    {
        RequireScriptHost();

        string[] exports = [.. SqliteExports(), "OPENSSL_init_crypto"];

        ScriptResult result = await RunAsync(withDumpbin: true, exports: exports, dependents: ManifestDependents());

        Assert.NotEqual(0, result.ExitCode);

        Assert.Contains("non-SQLite symbol", result.Output, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_win_asset_that_does_not_export_the_sqlite_api_fails()
    {
        RequireScriptHost();

        ScriptResult result = await RunAsync(withDumpbin: true, exports: ["sqlite3_close"], dependents: ManifestDependents());

        Assert.NotEqual(0, result.ExitCode);

        Assert.Contains("the SQLite C API is not exported", result.Output, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_win_asset_that_imports_an_undeclared_library_fails()
    {
        RequireScriptHost();

        string[] dependents = [.. ManifestDependents(), "SURPRISE.dll"];

        ScriptResult result = await RunAsync(withDumpbin: true, exports: SqliteExports(), dependents: dependents);

        Assert.NotEqual(0, result.ExitCode);

        Assert.Contains("undeclared dynamic dependency SURPRISE.dll", result.Output, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_declared_import_the_asset_no_longer_has_is_reported()
    {
        RequireScriptHost();

        string[] dependents = ManifestDependents()[1..];

        ScriptResult result = await RunAsync(withDumpbin: true, exports: SqliteExports(), dependents: dependents);

        Assert.NotEqual(0, result.ExitCode);

        Assert.Contains("declares a dynamic dependency the binary does not import", result.Output, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Without_dumpbin_the_win_checks_are_reported_unverified_rather_than_passed()
    {
        RequireScriptHost();

        ScriptResult result = await RunAsync(withDumpbin: false, exports: [], dependents: []);

        Assert.True(result.ExitCode == 0, result.Output);

        Assert.Contains("UNVERIFIED", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain($"{Rid} exports no symbol outside the SQLite C API", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Windows job exists to inspect the committed DLL, so a run there that could not find
    /// <c>dumpbin</c> (a runner image or toolchain-action change that stops the VC tools reaching the
    /// bash step) must fail rather than pass with two UNVERIFIED lines. <c>--strict</c> is that
    /// contract.
    /// </summary>
    [SkippableFact]
    public async Task Without_dumpbin_a_strict_run_fails_instead_of_reporting_unverified()
    {
        RequireScriptHost();

        ScriptResult result = await RunAsync(withDumpbin: false, exports: [], dependents: [], strict: true);

        Assert.True(result.ExitCode != 0, result.Output);

        Assert.Contains("--strict found 2 unverified check(s)", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("all checks passed", result.Output, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task With_dumpbin_a_strict_run_of_a_matching_asset_passes()
    {
        RequireScriptHost();

        ScriptResult result = await RunAsync(withDumpbin: true, exports: SqliteExports(), dependents: ManifestDependents(), strict: true);

        Assert.True(result.ExitCode == 0, result.Output);

        Assert.DoesNotContain("UNVERIFIED", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>dumpbin</c> prints no hint for an export that has no name, because hints index the name
    /// table, so such a row reads <c>ordinal RVA [NONAME]</c>. No allow-list by name can vouch for
    /// it, so it must count as a symbol outside the SQLite C API rather than being skipped.
    /// </summary>
    [SkippableFact]
    public async Task A_win_asset_with_an_ordinal_only_export_fails()
    {
        RequireScriptHost();

        ScriptResult result = await RunAsync(
            withDumpbin: true,
            exports: SqliteExports(),
            dependents: ManifestDependents(),
            rawExportRows: ["          9      00002000 [NONAME]"]);

        Assert.NotEqual(0, result.ExitCode);

        Assert.Contains("1 non-SQLite symbol(s) are exported", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bash a Windows runner ships has no <c>strings</c>, so the Windows job reads the compile
    /// options through the <c>grep</c> fallback. That is the only path the real job takes, and every
    /// other case here runs on a host that has <c>strings</c>.
    /// </summary>
    [SkippableFact]
    public async Task A_host_without_strings_still_proves_the_compile_options_through_the_grep_fallback()
    {
        RequireScriptHost();

        ScriptResult result = await RunAsync(withDumpbin: true, exports: SqliteExports(), dependents: ManifestDependents(), withStrings: false);

        Assert.True(result.ExitCode == 0, result.Output);

        Assert.Contains($"{Rid} compile options match the manifest", result.Output, StringComparison.Ordinal);

        Assert.False(
            File.Exists(Path.Combine(_root, "toolbelt", "strings")),
            "The fixture must hide strings, or the fallback was never taken.");
    }

    /// <summary>
    /// jq on Windows ends every output line with CRLF. Read into a bash variable or a
    /// <c>while read</c> loop, each value then carries a trailing carriage return, so a manifest path
    /// matched no license or SBOM entry and <c>jq -e</c> aborted the Windows job with exit 4 and no
    /// message. The stub delegates to the real jq and writes CRLF, as the Windows runner's jq does.
    /// </summary>
    [SkippableFact]
    public async Task A_manifest_only_run_passes_when_jq_writes_windows_line_endings()
    {
        RequireScriptHost();

        ScriptResult result = await RunAsync(withDumpbin: false, exports: [], dependents: [], manifestOnly: true, windowsJq: true);

        Assert.True(result.ExitCode == 0, result.Output);

        Assert.Contains("verify-native-sqlcipher: all checks passed.", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exact configuration of the Windows job: <c>--rid win-x64 --strict</c> with <c>dumpbin</c>
    /// on the path and jq writing CRLF. The declared dynamic dependencies are read through jq, so a
    /// stray carriage return there would also turn every declared import into a mismatch.
    /// </summary>
    [SkippableFact]
    public async Task A_strict_win_run_passes_when_jq_writes_windows_line_endings()
    {
        RequireScriptHost();

        ScriptResult result = await RunAsync(
            withDumpbin: true,
            exports: SqliteExports(),
            dependents: ManifestDependents(),
            strict: true,
            windowsJq: true);

        Assert.True(result.ExitCode == 0, result.Output);

        Assert.Contains($"{Rid} links only its declared dynamic dependencies", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("UNVERIFIED", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_windows_verification_job_runs_the_script_with_dumpbin_on_the_path()
    {
        string workflow = File
            .ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), ".github", "workflows", "verify-native-sqlcipher.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        int windows = workflow.IndexOf("\n  windows:", StringComparison.Ordinal);

        Assert.True(windows >= 0, "verify-native-sqlcipher.yml lost its Windows job.");

        string job = workflow[windows..];

        int toolchain = job.IndexOf("ilammy/msvc-dev-cmd@", StringComparison.Ordinal);

        int verify = job.IndexOf("./scripts/verify-native-sqlcipher.sh --rid", StringComparison.Ordinal);

        Assert.True(toolchain >= 0, "The Windows job no longer sets up the MSVC toolchain that provides dumpbin.");

        Assert.True(
            verify > toolchain,
            "The Windows job must run verify-native-sqlcipher.sh --rid after the MSVC toolchain is on the path, "
            + "or the exports and imports of the checked-in win-* asset are never inspected.");

        string command = job[verify..job.IndexOf('\n', verify)];

        Assert.Contains(" --strict", command, StringComparison.Ordinal);
    }

    private static void RequireScriptHost()
    {
        Skip.If(OperatingSystem.IsWindows(), "The stubbed-dumpbin fixture is POSIX-only; the real dumpbin runs in the Windows CI job.");

        foreach (string command in new[] { "bash", "jq", "awk" })
        {
            Skip.IfNot(IsOnPath(command), $"{command} is required to run verify-native-sqlcipher.sh.");
        }

        Skip.IfNot(
            IsOnPath("shasum") || IsOnPath("sha256sum"),
            "shasum or sha256sum is required to run verify-native-sqlcipher.sh.");
    }

    private static bool IsOnPath(string command) =>
        (global::System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, command)));

    private static string[] SqliteExports() =>
        ["sqlite3_open", "sqlite3_close", "sqlite3_key", "sqlite3_exec"];

    private static string[] ManifestDependents()
    {
        string manifestPath = Path.Combine(
            TestRepositoryPaths.RepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.NativeSqlCipher",
            "native-source-manifest.json");

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));

        foreach (JsonElement asset in manifest.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("rid").GetString() == Rid)
            {
                return asset
                    .GetProperty("dynamicDependencies")
                    .EnumerateArray()
                    .Select(static element => element.GetString()!)
                    .ToArray();
            }
        }

        throw new InvalidOperationException($"The manifest has no {Rid} asset.");
    }

    /// <summary>
    /// Real dumpbin output ends lines with CRLF and puts a blank line before <c>Summary</c>; the stub
    /// reproduces both so the script's parsing is the thing under test.
    /// </summary>
    private static string ExportsOutput(IEnumerable<string> exports, IEnumerable<string> rawRows)
    {
        StringBuilder text = new();

        text.Append("Microsoft (R) COFF/PE Dumper Version 14.44.35211.0\r\n");

        text.Append("Dump of file e_sqlcipher.dll\r\n\r\n");

        text.Append("File Type: DLL\r\n\r\n");

        text.Append("  Section contains the following exports for e_sqlcipher.dll\r\n\r\n");

        text.Append("    00000000 characteristics\r\n");

        text.Append("    ordinal hint RVA      name\r\n\r\n");

        int ordinal = 1;

        foreach (string export in exports)
        {
            text.Append(CultureFormat($"{ordinal,11} {ordinal - 1,4:X} {0x1000 + (ordinal * 16),8:X8} {export}\r\n"));

            ordinal++;
        }

        foreach (string row in rawRows)
        {
            text.Append(row).Append("\r\n");
        }

        text.Append("\r\n  Summary\r\n\r\n        1000 .data\r\n");

        return text.ToString();
    }

    private static string DependentsOutput(IEnumerable<string> dependents)
    {
        StringBuilder text = new();

        text.Append("Microsoft (R) COFF/PE Dumper Version 14.44.35211.0\r\n");

        text.Append("Dump of file e_sqlcipher.dll\r\n\r\n");

        text.Append("File Type: DLL\r\n\r\n");

        text.Append("  Image has the following dependencies:\r\n\r\n");

        foreach (string dependent in dependents)
        {
            text.Append("    ").Append(dependent).Append("\r\n");
        }

        text.Append("\r\n  Summary\r\n\r\n        1000 .data\r\n");

        return text.ToString();
    }

    private static string CultureFormat(FormattableString value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<ScriptResult> RunAsync(
        bool withDumpbin,
        IReadOnlyList<string> exports,
        IReadOnlyList<string> dependents,
        bool withStrings = true,
        bool strict = false,
        IReadOnlyList<string>? rawExportRows = null,
        bool manifestOnly = false,
        bool windowsJq = false)
    {
        string stubBin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;

        if (windowsJq)
        {
            await WriteWindowsJqStubAsync(stubBin);
        }

        if (withDumpbin)
        {
            string exportsFile = Path.Combine(_root, "exports.txt");

            string dependentsFile = Path.Combine(_root, "dependents.txt");

            await File.WriteAllTextAsync(exportsFile, ExportsOutput(exports, rawExportRows ?? []));

            await File.WriteAllTextAsync(dependentsFile, DependentsOutput(dependents));

            string stub = Path.Combine(stubBin, "dumpbin");

            await File.WriteAllTextAsync(
                stub,
                "#!/usr/bin/env bash\n"
                + "case \"${1:-}\" in\n"
                + $"  -exports | /exports) cat '{exportsFile}' ;;\n"
                + $"  -dependents | /dependents) cat '{dependentsFile}' ;;\n"
                + "  *) echo \"unexpected dumpbin arguments: $*\" >&2; exit 2 ;;\n"
                + "esac\n");

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    stub,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        System.Diagnostics.ProcessStartInfo start = new("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        start.ArgumentList.Add(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "scripts", "verify-native-sqlcipher.sh"));

        if (manifestOnly)
        {
            start.ArgumentList.Add("--manifest-only");
        }
        else
        {
            start.ArgumentList.Add("--rid");

            start.ArgumentList.Add(Rid);
        }

        if (strict)
        {
            start.ArgumentList.Add("--strict");
        }

        start.Environment["PATH"] = stubBin + Path.PathSeparator
            + (withStrings ? global::System.Environment.GetEnvironmentVariable("PATH") : BuildToolbeltWithoutStrings());

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("bash did not start.");

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);

        Task<string> standardError = process.StandardError.ReadToEndAsync(timeout.Token);

        await process.WaitForExitAsync(timeout.Token);

        return new ScriptResult(process.ExitCode, await standardOutput + await standardError);
    }

    /// <summary>
    /// A <c>jq</c> ahead of the real one on <c>PATH</c> that delegates to it and ends every output
    /// line with CRLF, as jq does on the Windows runner, keeping jq's own exit status.
    /// </summary>
    private static async Task WriteWindowsJqStubAsync(string stubBin)
    {
        string realJq = (global::System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(static directory => Path.Combine(directory, "jq"))
            .First(File.Exists);

        string stub = Path.Combine(stubBin, "jq");

        await File.WriteAllTextAsync(
            stub,
            "#!/usr/bin/env bash\n"
            + "set -o pipefail\n"
            + $"'{realJq}' \"$@\" | awk '{{ printf \"%s\\r\\n\", $0 }}'\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// A directory of symbolic links to every command on the current <c>PATH</c> except
    /// <c>strings</c>, so the script sees a host that has everything else but no binutils.
    /// </summary>
    private string BuildToolbeltWithoutStrings()
    {
        string toolbelt = Directory.CreateDirectory(Path.Combine(_root, "toolbelt")).FullName;

        HashSet<string> seen = new(StringComparer.Ordinal) { "strings" };

        foreach (string directory in (global::System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Directory.Exists))
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                string name = Path.GetFileName(file);

                if (!seen.Add(name))
                {
                    continue;
                }

                try
                {
                    File.CreateSymbolicLink(Path.Combine(toolbelt, name), file);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return toolbelt;
    }

    private sealed record ScriptResult(int ExitCode, string Output);
}
