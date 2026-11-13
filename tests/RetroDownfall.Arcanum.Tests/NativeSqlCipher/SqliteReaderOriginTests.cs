using System.Diagnostics;
using RetroDownfall.Arcanum.SqliteReaderSmoke;

namespace RetroDownfall.Arcanum.Tests.NativeSqlCipher;

public sealed class SqliteReaderOriginTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("wrong-platform")]
    [InlineData("healthy-with-override")]
    public async Task Initialization_uses_only_the_owned_asset_with_a_valid_probe_substitute(string shape)
    {
        string isolated = Path.Combine(Path.GetTempPath(), "arcanum-native-refusal-" + Guid.NewGuid().ToString("N"));

        string application = Path.Combine(isolated, "application");

        string decoy = Path.Combine(isolated, "decoy");

        Directory.CreateDirectory(application);

        Directory.CreateDirectory(decoy);

        string asset = OperatingSystem.IsWindows() ? "e_sqlcipher.dll" : "libe_sqlcipher.dylib";

        try
        {
            foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            {
                if (Path.GetFileName(file) != asset)
                {
                    File.Copy(file, Path.Combine(application, Path.GetFileName(file)));
                }
            }

            File.Copy(Path.Combine(AppContext.BaseDirectory, asset), Path.Combine(decoy, asset));

            File.Copy(Path.Combine(AppContext.BaseDirectory, asset), Path.Combine(decoy,
                OperatingSystem.IsWindows() ? "sqlcipher.dll" : "libsqlcipher.dylib"));

            if (shape == "corrupt")
            {
                File.WriteAllText(Path.Combine(application, asset), "not a native library");
            }
            else if (shape == "wrong-platform")
            {
                string otherRid = OperatingSystem.IsWindows() ? "osx-arm64" : "win-x64";

                File.Copy(NativeSqlCipherTestPaths.AssetPath(otherRid), Path.Combine(application, asset));
            }

            if (shape == "healthy-with-override")
            {
                File.Copy(Path.Combine(AppContext.BaseDirectory, asset), Path.Combine(application, asset));
            }

            await RunChild("native-decoy-control", application, decoy);

            if (shape == "healthy-with-override" && OperatingSystem.IsWindows())
            {
                // Windows absolute-path loading is unaffected by PATH; the owned image must work.
                await RunChild("computed-stream", application, decoy);

                return;
            }

            await RunChild("native-refusal", application, decoy);

            if (shape == "healthy-with-override")
            {
                await RunChild("computed-stream", application);

                if (OperatingSystem.IsMacOS())
                {
                    await RunChild("native-decoy-control-cleared-override", application, decoy);

                    await RunChild("native-refusal-cleared-override", application, decoy);

                    string alias = Path.Combine(isolated, "application-alias");

                    Directory.CreateSymbolicLink(alias, application);

                    await RunChild("computed-stream", alias);
                }
            }
        }
        finally
        {
            Directory.Delete(isolated, recursive: true);
        }
    }

    [Theory]
    [InlineData("cold-context")]
    [InlineData("enum-comparer")]
    [InlineData("design-time-after-runtime")]
    [InlineData("compiled-persistence")]
    [InlineData("computed-fixed")]
    [InlineData("computed-length")]
    [InlineData("computed-stream")]
    [InlineData("computed-alias")]
    [InlineData("single-expression")]
    [InlineData("table-zero")]
    [InlineData("table-three")]
    [InlineData("table-stream")]
    [InlineData("computed-empty")]
    [InlineData("null-value")]
    [InlineData("bad-ordinal")]
    [InlineData("negative-offset")]
    [InlineData("bad-buffer")]
    [InlineData("numeric-conversion")]
    [InlineData("cancelled")]
    [InlineData("disposed-reader")]
    [InlineData("disposed-stream")]
    public async Task Real_provider_returns_exact_bytes_and_preserves_table_streaming_in_a_contained_process(string mode)
    {
        await RunChild(mode, AppContext.BaseDirectory);
    }

    private static async Task RunChild(string mode, string output, string? decoy = null)
    {
        string dotnet = global::System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

        ProcessStartInfo start = new(OperatingSystem.IsWindows() || decoy is not null ? dotnet : "/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = output,
        };

        if (!OperatingSystem.IsWindows() && decoy is null)
        {
            start.ArgumentList.Add("-c");

            start.ArgumentList.Add("ulimit -c 0; exec \"$@\"");

            start.ArgumentList.Add("sqlite-reader-smoke");

            start.ArgumentList.Add(dotnet);
        }

        foreach (string argument in new[]
        {
            "exec", "--runtimeconfig", Path.Combine(output, "RetroDownfall.Arcanum.Tests.runtimeconfig.json"),
            "--depsfile", Path.Combine(output, "RetroDownfall.Arcanum.Tests.deps.json"),
            Path.Combine(output, Path.GetFileName(typeof(SqliteReaderSmokeEntryPoint).Assembly.Location)), mode,
        })
        {
            start.ArgumentList.Add(argument);
        }

        if (decoy is not null)
        {
            start.Environment[OperatingSystem.IsWindows() ? "PATH" : "DYLD_LIBRARY_PATH"] = decoy
                + Path.PathSeparator + global::System.Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "PATH" : "DYLD_LIBRARY_PATH");
        }

        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";

        start.Environment["COMPlus_DbgEnableMiniDump"] = "0";

        using global::System.Diagnostics.Process process = global::System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("The reader smoke process did not start.");

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);

        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);

            await process.WaitForExitAsync(CancellationToken.None);

            throw;
        }

        string actualOutput = await stdout;

        string actualError = await stderr;

        Assert.True(process.ExitCode == 0,
            $"Reader child {mode} exited {process.ExitCode}; stdout={actualOutput}; stderr={actualError}");

        Assert.Equal("SQLITE_READER_SMOKE_OK " + mode, actualOutput.Trim());
    }
}
