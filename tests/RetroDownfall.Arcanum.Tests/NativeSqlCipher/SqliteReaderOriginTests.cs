using System.Diagnostics;
using RetroDownfall.Arcanum.SqliteReaderSmoke;

namespace RetroDownfall.Arcanum.Tests.NativeSqlCipher;

public sealed class SqliteReaderOriginTests
{
    [Theory]
    [InlineData("cold-context")]
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
        string output = AppContext.BaseDirectory;

        string dotnet = global::System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

        ProcessStartInfo start = new(OperatingSystem.IsWindows() ? dotnet : "/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = output,
        };

        if (!OperatingSystem.IsWindows())
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
            typeof(SqliteReaderSmokeEntryPoint).Assembly.Location, mode,
        })
        {
            start.ArgumentList.Add(argument);
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
