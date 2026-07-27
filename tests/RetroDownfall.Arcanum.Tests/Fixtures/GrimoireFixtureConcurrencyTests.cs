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

        string templatePath = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            "grimoire-template",
            "template-remediation-v1.db");

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
