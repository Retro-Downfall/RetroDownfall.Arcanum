using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

[Collection("ProcessEnvironment")]
public sealed class GrimoireFixtureConcurrencyTests(GrimoireFixture fixture)
{
    [Fact]
    public void Ci_has_packaged_sqlcipher_native_asset()
    {
        if (!string.Equals(
                global::System.Environment.GetEnvironmentVariable("CI"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Assert.True(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);
    }

    [Fact]
    public void Probe_reports_unavailable_when_its_temp_directory_cannot_be_created()
    {

        string squatter = Path.Combine(Path.GetTempPath(), $"arcanum-probe-squatter-{Guid.NewGuid():N}");

        File.WriteAllText(squatter, "A file squats the probe's parent directory.");

        try
        {

            (bool available, string reason) = GrimoireFixture.ProbeSqlCipher(
                Path.Combine(squatter, "probe.db"),
                "probe-passphrase");

            Assert.False(available);

            Assert.NotEmpty(reason);

        }
        finally
        {

            File.Delete(squatter);

        }

    }

    [Fact]
    public void Probe_reports_unavailable_when_the_probe_database_cannot_be_opened()
    {

        string probePath = Path.Combine(Path.GetTempPath(), $"arcanum-probe-dir-{Guid.NewGuid():N}");

        Directory.CreateDirectory(probePath);

        try
        {

            (bool available, string reason) = GrimoireFixture.ProbeSqlCipher(probePath, "probe-passphrase");

            Assert.False(available);

            Assert.NotEmpty(reason);

        }
        finally
        {

            Directory.Delete(probePath, recursive: true);

        }

    }

    [SkippableFact]
    public void Probe_reports_available_and_removes_its_temp_database()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string probePath = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            $"probe-{Guid.NewGuid():N}.db");

        (bool available, string reason) = GrimoireFixture.ProbeSqlCipher(probePath, fixture.Passphrase);

        Assert.True(available, reason);

        Assert.Equal(string.Empty, reason);

        Assert.False(File.Exists(probePath));

    }

    [SkippableFact]
    public async Task CopyDatabase_waits_for_template_lifecycle_lock()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        object templateLock = typeof(GrimoireFixture)
            .GetField("BuildLock", BindingFlags.NonPublic | BindingFlags.Static)?
            .GetValue(null)
            ?? throw new InvalidOperationException("Template lifecycle lock was not found.");

        using ManualResetEventSlim copyStarted = new();

        Task<string> copyTask;

        Monitor.Enter(templateLock);

        try
        {

            copyTask = Task.Run(() =>
            {

                copyStarted.Set();

                return fixture.CopyDatabase();

            });

            Assert.True(copyStarted.Wait(TimeSpan.FromSeconds(5)));

            Assert.False(
                copyTask.Wait(TimeSpan.FromMilliseconds(500)),
                "CopyDatabase completed while template remediation held its lifecycle lock.");

        }
        finally
        {

            Monitor.Exit(templateLock);

        }

        string copyPath = await copyTask;

        Assert.True(File.Exists(copyPath));

        Assert.True(File.Exists(copyPath + ".kdf"));

    }

    [SkippableFact]
    public async Task Concurrent_template_rebuild_and_copies_produce_complete_databases()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string templatePath = GrimoireFixture.TemplatePath;

        string sidecarPath = templatePath + ".kdf";

        string fingerprintPath = templatePath + ".fingerprint";

        Assert.True(File.Exists(templatePath));

        Assert.True(File.Exists(sidecarPath));

        File.Delete(fingerprintPath);

        Task<GrimoireFixture> rebuildTask = Task.Run(static () => new GrimoireFixture());

        Assert.True(
            SpinWait.SpinUntil(() => !File.Exists(sidecarPath), TimeSpan.FromSeconds(10)),
            "The concurrent fixture did not enter template remediation.");

        Task<string>[] copyTasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(fixture.CopyDatabase))
            .ToArray();

        GrimoireFixture rebuilt = await rebuildTask;

        try
        {

            string[] copies = await Task.WhenAll(copyTasks);

            foreach (string copyPath in copies)
            {

                Assert.True(File.Exists(copyPath), copyPath);

                Assert.True(File.Exists(copyPath + ".kdf"), copyPath + ".kdf");

                await using var context = fixture.CreateContext(copyPath);

                Assert.True(await context.Database.CanConnectAsync());

            }

        }
        finally
        {

            rebuilt.Dispose();

        }

    }

}
