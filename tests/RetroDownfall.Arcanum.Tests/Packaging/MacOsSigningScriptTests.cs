using System.Diagnostics;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>
/// Executes <c>sign_publish_dir</c> and <c>sign_app_bundle</c> from scripts/packaging/macos/common.sh
/// against staged trees of real Mach-O files with <c>codesign</c> stubbed out. The release lane is the
/// only place this code ever runs, so a static read of the script is not enough: the failure this pins
/// is a GNU-only pipeline that produced an empty result under BSD userland, which reads as correct
/// until it runs.
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
    /// The <c>.app</c> path signs the same tree: build-app-dmg.sh copies the publish directory verbatim
    /// into <c>Contents/MacOS/</c>, so a nested dylib is exactly as deep there as it is in the CLI zip.
    /// A plain <c>find</c> walk is pre-order, not depth-first, so it hands back shallow files before the
    /// subdirectories it has not descended into yet; ordering has to be imposed after the walk. What
    /// gets ordered is every file in that directory, not every Mach-O: codesign seals a bundle's
    /// executables directory by location, so the contract is that nothing under it is left out.
    /// </summary>
    [SkippableFact]
    public void Sign_app_bundle_signs_every_file_under_contents_macos_deepest_first()
    {

        Skip.IfNot(OperatingSystem.IsMacOS(), "sign_app_bundle is a macOS packaging primitive.");

        string root = Directory.CreateTempSubdirectory("arcanum-sign-app-").FullName;

        try
        {

            string appPath = Path.Combine(root, "Arcanum.app");

            string macosDirectory = Path.Combine(appPath, "Contents", "MacOS");

            _ = Directory.CreateDirectory(macosDirectory);

            File.WriteAllText(
                Path.Combine(appPath, "Contents", "Info.plist"),
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>CFBundleExecutable</key>
                    <string>arcanum</string>
                    <key>CFBundleIdentifier</key>
                    <string>com.retrodownfall.arcanum.probe</string>
                </dict>
                </plist>
                """);

            string main = Path.Combine(macosDirectory, "arcanum");

            List<string> shallow = [];

            foreach (string name in new[] { "libalpha.dylib", "libbeta.dylib", "libgamma.dylib" })
            {

                shallow.Add(Path.Combine(macosDirectory, name));

            }

            List<string> deep = [];

            foreach (string name in new[] { "one", "two", "three" })
            {

                string directory = Path.Combine(macosDirectory, "nested-" + name, "native");

                _ = Directory.CreateDirectory(directory);

                deep.Add(Path.Combine(directory, "lib" + name + ".dylib"));

            }

            // /bin/echo is a real Mach-O, standing in for the dylibs and apphost a bundle really holds.
            foreach (string target in shallow.Concat(deep).Append(main))
            {

                File.Copy("/bin/echo", target);

            }

            // A managed assembly must reach codesign. The first release to get this far signed and
            // notarized the CLI, then failed sealing the .app with "code object is not signed at all /
            // In subcomponent: Microsoft.CSharp.dll". It is left as a non-Mach-O file so that nothing
            // but the signer's own rule can account for its being signed.
            string managedAssembly = Path.Combine(macosDirectory, "RetroDownfall.Arcanum.Cli.dll");

            File.WriteAllText(managedAssembly, "not mach-o");

            IReadOnlyList<string> signed = RunSigningFunction(root, "sign_app_bundle", appPath);

            // The bundle is sealed last, over an apphost that is itself signed after everything it loads.
            Assert.Equal(appPath, signed[^1]);

            Assert.Equal(main, signed[^2]);

            string[] nested = [.. signed.Take(signed.Count - 2)];

            Assert.Equal(
                [.. shallow.Concat(deep).Append(managedAssembly).Order(StringComparer.Ordinal)],
                [.. nested.Order(StringComparer.Ordinal)]);

            int[] depths = [.. nested.Select(static path => path.Count(static c => c == '/'))];

            for (int index = 1; index < depths.Length; index++)
            {

                Assert.True(
                    depths[index] <= depths[index - 1],
                    $"'{nested[index]}' was signed after the shallower '{nested[index - 1]}', so a dependent "
                    + "was sealed before the library it loads.");

            }

        }
        finally
        {

            Directory.Delete(root, recursive: true);

        }

    }

    /// <summary>
    /// Runs the harness and returns the paths handed to <c>codesign</c> for signing, in order.
    /// </summary>
    private static IReadOnlyList<string> RunSignPublishDir(string root, string stage) =>
        RunSigningFunction(root, "sign_publish_dir", stage);

    /// <summary>
    /// Sources common.sh under <c>/bin/bash</c>, calls one signing function against <paramref name="target"/>
    /// with <c>codesign</c> stubbed out, and returns the paths it was asked to sign, in order.
    /// </summary>
    private static IReadOnlyList<string> RunSigningFunction(string root, string function, string target)
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
            "$4" "$1" ""
            """);

        ProcessStartInfo startInfo = new("/bin/bash")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add(harness);

        startInfo.ArgumentList.Add(target);

        startInfo.ArgumentList.Add(binDirectory);

        startInfo.ArgumentList.Add(CommonScript());

        startInfo.ArgumentList.Add(function);

        startInfo.Environment["CODESIGN_LOG"] = log;

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo)!;

        string standardOutput = process.StandardOutput.ReadToEnd();

        string standardError = process.StandardError.ReadToEnd();

        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"{function} exited {process.ExitCode}, so the release lane produces no artifact."
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
