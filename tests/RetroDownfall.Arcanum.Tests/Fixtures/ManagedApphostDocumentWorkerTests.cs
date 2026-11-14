using System.Security.Cryptography;
using System.Runtime.InteropServices;

using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;
using RetroDownfall.Arcanum.Tests.Process;
using RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

public sealed class ManagedApphostDocumentWorkerTests
{
    [SkippableFact]
    public async Task Same_build_managed_worker_starts_and_emits_a_pdf_frame_inside_the_required_jail()
    {
        Skip.IfNot(MacOsSandboxProbe.IsSandboxExecRunnable(), "Needs a macOS host where sandbox-exec can apply a profile.");

        string apphost = ManagedApphostDocumentWorker.ResolveApphost(
            Path.Combine(AppContext.BaseDirectory, "document-worker.sha256"), AppContext.BaseDirectory);

        string dotnetRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));

        List<string> roots = [dotnetRoot];

        if (dotnetRoot.StartsWith("/opt/homebrew/", StringComparison.Ordinal)) roots.Add("/opt/homebrew/");

        DocumentWorkerLaunch launch = DocumentWorkerLaunch.ForTests(apphost,
            new Dictionary<string, string> { ["DOTNET_ROOT"] = dotnetRoot }, roots, 256);

        DocumentWorkerProfile profile = DocumentWorkerProfile.For(DocumentWorkerFormat.Pdf);

        DocumentWorkerRunRequest request = ProcessDocumentExtractionWorker.CreateRunRequest(launch, profile);

        CappedChildProcessRunResult run = await CappedChildProcessRunner.RunAsync(
            request.StartInfo, launch.EnvironmentProfile, request.TotalOutputCapBytes, request.WallClock,
            request.Limits, new ProcessResourceLimiter(), CancellationToken.None, request.Sandbox,
            standardInput: PdfFixtures.Read("rotated-text.pdf"), rawStandardOutput: true,
            maxFileSizeBytes: request.MaxFileSizeBytes);

        DocumentWorkerResult mapped = ProcessDocumentExtractionWorker.Map(run, profile);

        Assert.True(mapped.FailureReason is null,
            $"Outcome={run.Outcome}; ExitCode={run.ExitCode}; Stderr={run.Stderr.Text}; Failure={mapped.FailureReason}");

        Assert.NotNull(mapped.Outcome);

        Assert.NotEmpty(mapped.Outcome.Pages);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("instrumented-output")]
    [InlineData("no-apphost")]
    [InlineData("unrecorded-file")]
    public void Only_the_same_build_closure_outside_test_output_is_accepted(string scenario)
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("arcanum-document-worker-manifest-");

        try
        {
            string worker = Directory.CreateDirectory(Path.Combine(root.FullName, "worker")).FullName;

            string testOutput = Directory.CreateDirectory(Path.Combine(root.FullName, "tests")).FullName;

            string apphost = Path.Combine(scenario == "instrumented-output" ? testOutput : worker, "RetroDownfall.Arcanum.Cli");

            string dependency = Path.Combine(worker, "RetroDownfall.Arcanum.Infrastructure.dll");

            File.WriteAllText(apphost, "same-build apphost");

            File.WriteAllText(dependency, "same-build dependency");

            string manifest = Path.Combine(root.FullName, "document-worker.sha256");

            File.WriteAllLines(manifest, (scenario == "no-apphost" ? new[] { dependency } : [apphost, dependency])
                .Select(path => $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))} {path}"));

            if (scenario == "changed")
            {
                File.WriteAllText(dependency, "different build dependency");
            }

            if (scenario == "missing")
            {
                File.Delete(dependency);
            }

            if (scenario == "unrecorded-file")
            {
                File.WriteAllText(Path.Combine(worker, "unrecorded.dll"), "not part of this build");
            }

            if (scenario == "current")
            {
                Assert.Equal(apphost, ManagedApphostDocumentWorker.ResolveApphost(manifest, testOutput));
            }
            else if (scenario == "missing")
            {
                Assert.Throws<FileNotFoundException>(() => ManagedApphostDocumentWorker.ResolveApphost(manifest, testOutput));
            }
            else
            {
                Assert.Throws<InvalidDataException>(() => ManagedApphostDocumentWorker.ResolveApphost(manifest, testOutput));
            }
        }
        finally
        {
            TestDirectoryCleanup.TryDelete(root.FullName, nameof(ManagedApphostDocumentWorkerTests));
        }
    }
}
