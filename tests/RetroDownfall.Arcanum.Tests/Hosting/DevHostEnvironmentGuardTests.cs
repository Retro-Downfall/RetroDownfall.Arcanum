using System.Diagnostics;
using SystemProcess = System.Diagnostics.Process;

namespace RetroDownfall.Arcanum.Tests.Hosting;

/// <summary>
/// The DevHost is a development convenience that prints the master API key it generates, so it does not
/// run anywhere else.
/// </summary>
public sealed class DevHostEnvironmentGuardTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task DevHost_refuses_to_start_outside_Development_or_Testing(string environment)
    {
        string devHost = typeof(Program).Assembly.Location;

        string home = Path.Combine(Path.GetTempPath(), "arcanum-tests", $"devhost-guard-{Guid.NewGuid():N}");

        _ = Directory.CreateDirectory(home);

        try
        {
            ProcessStartInfo start = new(
                global::System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            start.ArgumentList.Add(devHost);

            // The child must never reach the developer's real profile, whatever it decides to do.
            start.Environment["HOME"] = home;

            start.Environment["USERPROFILE"] = home;

            start.Environment["ASPNETCORE_ENVIRONMENT"] = environment;

            start.Environment["DOTNET_ENVIRONMENT"] = environment;

            start.Environment.Remove("ARCANUM_TEST_HOME");

            using SystemProcess process = SystemProcess.Start(start)!;

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

            Task<string> standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);

                Assert.Fail($"The DevHost kept running in a '{environment}' environment instead of refusing to start.");
            }

            string error = await standardError;

            Assert.Equal(2, process.ExitCode);

            Assert.Contains("Development or Testing", error, StringComparison.Ordinal);

            Assert.DoesNotContain("listening", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(home, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a unique temp directory.
            }
        }
    }
}
