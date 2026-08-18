using System.Diagnostics;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>
/// Executes <c>sign_publish_dir</c> from scripts/packaging/macos/common.sh against a staged tree of
/// real Mach-O files with <c>codesign</c> stubbed out. The release lane is the only place this code
/// ever runs, so a static read of the script is not enough: the failure this pins is a GNU-only
/// pipeline that produced an empty result under BSD userland, which reads as correct until it runs.
/// </summary>
public sealed class MacOsSigningScriptTests
{

    /// <summary>
    /// Nested Mach-O files must be signed deepest-first and the main executable last, and the
    /// function must actually sign every one of them. bash 3.2 is the macOS runner shell, so the
    /// harness runs under /bin/bash rather than whatever newer bash a developer has on PATH.
    /// </summary>
    [SkippableFact]
    public void Sign_publish_dir_signs_every_nested_macho_deepest_first()
    {

        Skip.IfNot(OperatingSystem.IsMacOS(), "sign_publish_dir is a macOS packaging primitive.");

        string root = Directory.CreateTempSubdirectory("arcanum-sign-publish-").FullName;

        try
        {

            string stage = Path.Combine(root, "stage");

            string deep = Path.Combine(stage, "runtimes", "osx-arm64", "native");

            _ = Directory.CreateDirectory(deep);

            string main = Path.Combine(stage, "arcanum");

            string shallowLibrary = Path.Combine(stage, "libe_sqlcipher.dylib");

            string deepLibrary = Path.Combine(deep, "libnested.dylib");

            // /bin/echo is a real Mach-O, which is what the `file -b | grep Mach-O` filter selects on.
            foreach (string target in new[] { main, shallowLibrary, deepLibrary })
            {

                File.Copy("/bin/echo", target);

            }

            // A managed assembly is not Mach-O and must never reach codesign.
            File.WriteAllText(Path.Combine(stage, "RetroDownfall.Arcanum.Cli.dll"), "not mach-o");

            IReadOnlyList<string> signed = RunSignPublishDir(root, stage);

            Assert.Equal(
                [deepLibrary, shallowLibrary, main],
                signed);

        }
        finally
        {

            Directory.Delete(root, recursive: true);

        }

    }

    /// <summary>
    /// Runs the harness and returns the paths handed to <c>codesign</c> for signing, in order.
    /// </summary>
    private static IReadOnlyList<string> RunSignPublishDir(string root, string stage)
    {

        string binDirectory = Path.Combine(root, "bin");

        _ = Directory.CreateDirectory(binDirectory);

        string log = Path.Combine(root, "codesign.log");

        string stub = Path.Combine(binDirectory, "codesign");

        WriteExecutable(
            stub,
            """
            #!/bin/bash
            target="${@: -1}"
            if [ "$1" = "--verify" ]; then
              printf 'verify %s\n' "$target" >> "$CODESIGN_LOG"
            else
              printf 'sign %s\n' "$target" >> "$CODESIGN_LOG"
            fi
            """);

        string harness = Path.Combine(root, "harness.sh");

        WriteExecutable(
            harness,
            """
            #!/bin/bash
            set -euo pipefail
            export PATH="$2:$PATH"
            export APPLE_SIGNING_IDENTITY="Developer ID Application: Probe (PROBETEAM)"
            # shellcheck source=/dev/null
            source "$3"
            sign_publish_dir "$1" ""
            """);

        ProcessStartInfo startInfo = new("/bin/bash")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add(harness);

        startInfo.ArgumentList.Add(stage);

        startInfo.ArgumentList.Add(binDirectory);

        startInfo.ArgumentList.Add(CommonScript());

        startInfo.Environment["CODESIGN_LOG"] = log;

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo)!;

        string standardOutput = process.StandardOutput.ReadToEnd();

        string standardError = process.StandardError.ReadToEnd();

        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"sign_publish_dir exited {process.ExitCode}, so the release lane produces no artifact."
            + global::System.Environment.NewLine
            + standardOutput
            + standardError);

        Assert.True(File.Exists(log), "codesign was never invoked.");

        return File.ReadAllLines(log)
            .Where(static line => line.StartsWith("sign ", StringComparison.Ordinal))
            .Select(static line => line["sign ".Length..])
            .ToArray();

    }

    private static void WriteExecutable(string path, string contents)
    {

        File.WriteAllText(path, contents + global::System.Environment.NewLine);

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }

    }

    private static string CommonScript()
    {

        string path = Path.Combine(
            RepositoryRoot(),
            "scripts",
            "packaging",
            "macos",
            "common.sh");

        Assert.True(File.Exists(path), $"Missing macOS packaging helpers: {path}");

        return path;

    }

    private static string RepositoryRoot()
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
