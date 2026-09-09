using System.Security.Cryptography;

using System.Text;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed partial class GrimoireAdmissionBenchmarkPackagingTests
{
    [SkippableTheory]
    [InlineData("physical")]
    [InlineData("alias")]
    [InlineData("trailing-slash")]
    [InlineData("unset")]
    [InlineData("empty")]
    public async Task Workspace_uses_physical_paths_and_removes_only_its_created_directory(string parentKind)
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "Requires macOS filesystem semantics.");

        string fixture = await CreatePhysicalFixtureAsync();

        try
        {
            string parent = Path.Combine(fixture, "physical parent with spaces");

            Directory.CreateDirectory(parent);

            string alias = Path.Combine(fixture, "alias with spaces");

            Directory.CreateSymbolicLink(alias, parent);

            string sentinel = Path.Combine(parent, "arcanum-grimoire-admission-script.sentinel");

            Directory.CreateDirectory(sentinel);

            string? temporaryParent = parentKind switch
            {
                "alias" => alias,
                "trailing-slash" => alias + "/",
                "unset" => null,
                "empty" => string.Empty,
                _ => parent,
            };

            ProcessResult result = await RunLauncherFunctionsAsync(
                FindRepositoryRoot(),
                "create_workspace\nprintf '%s\\n' \"$temp_root\"\ncleanup\ncleanup",
                [],
                new Dictionary<string, string?> { ["TMPDIR"] = temporaryParent });

            Assert.Equal(0, result.ExitCode);

            string expectedParent = parentKind is "unset" or "empty"
                ? (await RunProcessAsync("/bin/pwd", ["-P"], "/tmp")).StandardOutput.Trim()
                : parent;

            string workspace = result.StandardOutput.Trim();

            Assert.StartsWith(expectedParent + "/arcanum-grimoire-admission-script.", workspace, StringComparison.Ordinal);

            Assert.Equal(expectedParent, Path.GetDirectoryName(workspace));

            Assert.False(Directory.Exists(workspace));

            Assert.True(Directory.Exists(sentinel));
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    [SkippableTheory]
    [InlineData("retarget-alias")]
    [InlineData("replace-with-symlink")]
    [InlineData("replace-with-directory")]
    public async Task Cleanup_uses_original_physical_identity_and_refuses_replaced_workspaces(string replacement)
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "Requires macOS filesystem semantics.");

        string fixture = await CreatePhysicalFixtureAsync();

        try
        {
            string parent = Path.Combine(fixture, "physical parent");

            string other = Path.Combine(fixture, "other parent");

            Directory.CreateDirectory(parent);

            Directory.CreateDirectory(other);

            string alias = Path.Combine(fixture, "mutable alias");

            Directory.CreateSymbolicLink(alias, parent);

            string body = replacement switch
            {
                "retarget-alias" => "rm -- \"$TMPDIR\"\nln -s \"$1\" \"$TMPDIR\"",
                "replace-with-symlink" => "mv -- \"$temp_root\" \"$temp_root-original\"\nln -s \"$1\" \"$temp_root\"",
                _ => "mv -- \"$temp_root\" \"$temp_root-original\"\nmkdir -- \"$temp_root\"\nprintf sentinel > \"$temp_root/sentinel\"",
            };

            string sentinel = Path.Combine(other, "sentinel");

            await File.WriteAllTextAsync(sentinel, "sentinel");

            ProcessResult result = await RunLauncherFunctionsAsync(
                FindRepositoryRoot(),
                "create_workspace\nprintf '%s\\n' \"$temp_root\"\n" + body + "\ncleanup",
                [other],
                new Dictionary<string, string?> { ["TMPDIR"] = replacement == "retarget-alias" ? alias : parent });

            Assert.Equal(0, result.ExitCode);

            string workspace = result.StandardOutput.Trim();

            Assert.Equal(parent, Path.GetDirectoryName(workspace));

            Assert.Equal("sentinel", await File.ReadAllTextAsync(sentinel));

            if (replacement == "retarget-alias")
            {
                Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
            }
            else
            {
                Assert.True(Directory.Exists(workspace));

                Assert.True(Directory.Exists(workspace + "-original"));

                Assert.Equal("sentinel", await File.ReadAllTextAsync(Path.Combine(workspace, "sentinel")));
            }
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workspace_rejects_missing_or_nondirectory_temp_parent_before_creating_anything(bool useFile)
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "Requires macOS filesystem semantics.");

        string fixture = await CreatePhysicalFixtureAsync();

        try
        {
            string parent = Path.Combine(fixture, "invalid parent");

            if (useFile)
            {
                await File.WriteAllTextAsync(parent, "sentinel");
            }

            ProcessResult result = await RunLauncherFunctionsAsync(
                FindRepositoryRoot(),
                "create_workspace\nprintf 'unexpected success'",
                [],
                new Dictionary<string, string?> { ["TMPDIR"] = parent });

            Assert.Equal(2, result.ExitCode);

            Assert.Empty(result.StandardOutput);

            Assert.Equal(useFile ? 1 : 0, Directory.EnumerateFileSystemEntries(fixture).Count());
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    [SkippableTheory]
    [InlineData("symlink")]
    [InlineData("outside-parent")]
    [InlineData("unexpected-name")]
    [InlineData("nested-child")]
    public async Task Workspace_refuses_unconfined_or_symlinked_mktemp_results(string resultKind)
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "Requires macOS filesystem semantics.");

        string fixture = await CreatePhysicalFixtureAsync();

        try
        {
            string parent = Path.Combine(fixture, "physical parent");

            string other = Path.Combine(fixture, "other parent");

            string fakeBin = Path.Combine(fixture, "bin");

            Directory.CreateDirectory(parent);

            Directory.CreateDirectory(other);

            Directory.CreateDirectory(fakeBin);

            string returned = resultKind switch
            {
                "outside-parent" => Path.Combine(other, "arcanum-grimoire-admission-script.123456"),
                "unexpected-name" => Path.Combine(parent, "unowned-directory"),
                "nested-child" => Path.Combine(parent, "arcanum-grimoire-admission-script.outer1", "arcanum-grimoire-admission-script.123456"),
                _ => Path.Combine(parent, "arcanum-grimoire-admission-script.123456"),
            };

            if (resultKind == "symlink")
            {
                Directory.CreateSymbolicLink(returned, other);
            }
            else
            {
                Directory.CreateDirectory(returned);
            }

            string sentinel = Path.Combine(returned, "sentinel");

            await File.WriteAllTextAsync(sentinel, "sentinel");

            await WriteExecutableAsync(Path.Combine(fakeBin, "mktemp"), "#!/bin/sh\nprintf '%s\\n' \"$RETURNED_WORKSPACE\"\n");

            ProcessResult result = await RunLauncherFunctionsAsync(
                FindRepositoryRoot(),
                "create_workspace\nprintf 'unexpected success'",
                [],
                new Dictionary<string, string?>
                {
                    ["TMPDIR"] = parent,
                    ["RETURNED_WORKSPACE"] = returned,
                    ["PATH"] = fakeBin + ":/usr/bin:/bin",
                });

            Assert.Equal(2, result.ExitCode);

            Assert.Empty(result.StandardOutput);

            Assert.Equal("sentinel", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Corrected_launcher_refuses_historical_H_B_C_instrument_bytes()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "Requires macOS filesystem semantics.");

        const string harness = "f51ac3f84c3b408510e448311d7a5e15bdbc041e";

        const string baseline = "b0be2b4df8855e56f2dcfcaf155dfacddc51b88a";

        const string candidate = "09da72dad77dc5b829f688f33ed6b8562621aa8d";

        const string historicalScriptSha256 = "AA36EFE1ECDC0D1E5A7272A3213534BD5D13AADC504BDE07FE5EBDE68E69ECEF";

        foreach (string revision in new[] { harness, baseline, candidate })
        {
            ProcessResult historical = await RunProcessAsync(
                "git",
                ["show", revision + ":scripts/benchmark-grimoire-admission.sh"],
                FindRepositoryRoot());

            Assert.Equal(0, historical.ExitCode);

            Assert.Equal(
                historicalScriptSha256,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(historical.StandardOutput))));
        }

        ProcessResult result = await RunLauncherFunctionsAsync(
            FindRepositoryRoot(),
            "create_workspace\nprintf 'R\\tscripts/benchmark-grimoire-admission.sh\\n' > \"$temp_root/catalog-$1.txt\"\nrequire_instrument_bytes \"$1\" \"$2\" \"$3\"",
            [harness, baseline, candidate]);

        Assert.Equal(2, result.ExitCode);
    }

    private static async Task<string> CreatePhysicalFixtureAsync()
    {
        string fixture = Directory.CreateTempSubdirectory("arcanum-admission-portability-").FullName;

        ProcessResult physical = await RunProcessAsync("/bin/pwd", ["-P"], fixture);

        Assert.Equal(0, physical.ExitCode);

        return physical.StandardOutput.Trim();
    }

    private static async Task<ProcessResult> RunLauncherFunctionsAsync(
        string root,
        string body,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        string scriptPath = Path.Combine(root, "scripts", "benchmark-grimoire-admission.sh");

        string script = await File.ReadAllTextAsync(scriptPath);

        const string dispatcher = "[ $# -ge 1 ] || invalid 'Expected --smoke, --calibrate, or --qualify.'";

        int dispatcherIndex = script.IndexOf(dispatcher, StringComparison.Ordinal);

        Assert.True(dispatcherIndex > 0, "The launcher function/dispatcher boundary was not found.");

        return await RunProcessAsync(
            "/bin/sh",
            ["-c", script[..dispatcherIndex] + body, scriptPath, .. arguments],
            root,
            environment);
    }
}
