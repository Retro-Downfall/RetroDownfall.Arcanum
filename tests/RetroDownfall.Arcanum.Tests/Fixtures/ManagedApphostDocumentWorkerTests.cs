using System.Security.Cryptography;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

public sealed class ManagedApphostDocumentWorkerTests
{
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
