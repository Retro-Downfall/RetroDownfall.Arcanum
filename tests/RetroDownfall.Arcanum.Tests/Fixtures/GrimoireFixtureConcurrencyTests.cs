using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Infrastructure.Data;
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

    /// <summary>
    /// The template used to live at a single machine-global path, so two test processes sharing one
    /// temp directory — two developers, a CI agent running two jobs, or a filtered run alongside a
    /// full one — deleted and rebuilt each other's template mid-copy. That produced "file being used
    /// by another process" and missing <c>.db.kdf</c> failures across ~158 unrelated suites, none of
    /// which had anything to do with either change. The template directory must therefore be private
    /// to this process.
    /// </summary>
    [Fact]
    public void Template_directory_is_private_to_this_test_process()
    {
        string shared = Path.Combine(Path.GetTempPath(), "arcanum-tests", "grimoire-template");

        Assert.NotEqual(shared, GrimoireFixture.TemplateDirectory);

        Assert.Contains(
            global::System.Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Path.GetFileName(GrimoireFixture.TemplateDirectory),
            StringComparison.Ordinal);

        Assert.StartsWith(
            Path.Combine(Path.GetTempPath(), "arcanum-tests"),
            GrimoireFixture.TemplateDirectory,
            StringComparison.Ordinal);

        Assert.StartsWith(
            GrimoireFixture.TemplateDirectory + Path.DirectorySeparatorChar,
            GrimoireFixture.TemplatePath,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A killed test process leaves its per-copy database files, its SQLCipher probe, its API-host
    /// profile directory and its bare workspace directory behind, and only the template directory used
    /// to be collected. Every shape it can leave must be swept once it is older than the grace period,
    /// and nothing younger, nothing unrelated, and nothing another suite owns may be touched.
    /// </summary>
    [Fact]
    public void Sweep_collects_abandoned_copy_probe_and_profile_files()
    {
        string root = Path.Combine(Path.GetTempPath(), $"arcanum-sweep-{Guid.NewGuid():N}");

        Directory.CreateDirectory(root);

        try
        {
            DateTime abandoned = DateTime.UtcNow - TimeSpan.FromHours(13);

            string[] abandonedFiles =
            [
                $"grimoire-{Guid.NewGuid():N}.db",
                $"grimoire-{Guid.NewGuid():N}.db.kdf",
                $"grimoire-{Guid.NewGuid():N}.db-wal",
                $"grimoire-{Guid.NewGuid():N}.db-shm",
                $"probe-{Guid.NewGuid():N}.db",
            ];

            string[] abandonedDirectories =
            [
                $"grimoire-template-4242-{Guid.NewGuid():N}",
                $"api-host-{Guid.NewGuid():N}",
                Guid.NewGuid().ToString("N"),
            ];

            string[] youngFiles =
            [
                $"grimoire-{Guid.NewGuid():N}.db",
                $"probe-{Guid.NewGuid():N}.db",
            ];

            string[] youngDirectories =
            [
                $"api-host-{Guid.NewGuid():N}",
                Guid.NewGuid().ToString("N"),
            ];

            string[] oldButUnrelatedFiles = ["notes.txt", $"sidecar-{Guid.NewGuid():N}.db"];

            string[] oldButUnrelatedDirectories = [$"sidecar-{Guid.NewGuid():N}", "not-a-guid-directory"];

            foreach (string name in abandonedFiles.Concat(oldButUnrelatedFiles))
            {
                SeedFile(root, name, abandoned);
            }

            foreach (string name in abandonedDirectories.Concat(oldButUnrelatedDirectories))
            {
                SeedDirectory(root, name, abandoned);
            }

            foreach (string name in youngFiles)
            {
                SeedFile(root, name, DateTime.UtcNow);
            }

            foreach (string name in youngDirectories)
            {
                SeedDirectory(root, name, DateTime.UtcNow);
            }

            int removed = GrimoireFixture.SweepAbandonedTestArtifacts(root, TimeSpan.FromHours(12));

            Assert.Equal(abandonedFiles.Length + abandonedDirectories.Length, removed);

            foreach (string name in abandonedFiles)
            {
                Assert.False(File.Exists(Path.Combine(root, name)), name);
            }

            foreach (string name in abandonedDirectories)
            {
                Assert.False(Directory.Exists(Path.Combine(root, name)), name);
            }

            foreach (string name in youngFiles.Concat(oldButUnrelatedFiles))
            {
                Assert.True(File.Exists(Path.Combine(root, name)), name);
            }

            foreach (string name in youngDirectories.Concat(oldButUnrelatedDirectories))
            {
                Assert.True(Directory.Exists(Path.Combine(root, name)), name);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static void SeedFile(string root, string name, DateTime lastWriteUtc)
        {
            string path = Path.Combine(root, name);

            File.WriteAllText(path, "abandoned test artifact");

            File.SetLastWriteTimeUtc(path, lastWriteUtc);
        }

        static void SeedDirectory(string root, string name, DateTime lastWriteUtc)
        {
            string path = Path.Combine(root, name);

            Directory.CreateDirectory(path);

            File.WriteAllText(Path.Combine(path, "child.db"), "abandoned test artifact");

            Directory.SetLastWriteTimeUtc(path, lastWriteUtc);
        }
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

    /// <summary>
    /// A failed open can still have created the file (SQLite creates it as the connection opens), so the
    /// probe removes it on every path rather than only after a success.
    /// </summary>
    [SkippableFact]
    public void A_failed_probe_leaves_no_database_file_behind()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string directory = Path.Combine(Path.GetTempPath(), $"arcanum-probe-failed-{Guid.NewGuid():N}");

        string probePath = Path.Combine(directory, "probe.db");

        try
        {
            (bool available, string reason) = GrimoireFixture.ProbeSqlCipher(
                probePath,
                "probe-passphrase",
                static (path, _) =>
                {
                    File.WriteAllText(path, "created before the open failed");

                    throw new InvalidOperationException("encryption is not supported by this build");
                });

            Assert.False(available);

            Assert.Contains("encryption is not supported by this build", reason, StringComparison.Ordinal);

            Assert.False(File.Exists(probePath), "The failed probe left its database file behind.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The probe runs from the static constructor, where an escaping exception fails the type initializer for
    /// good, so its cleanup must not throw for any reason: a failure to delete is reported and the probe's
    /// own verdict stands.
    /// </summary>
    [Fact]
    public void A_probe_whose_cleanup_fails_for_any_reason_still_reports_its_verdict()
    {
        List<string> reports = [];

        GrimoireFixture.TryDeleteProbe(
            "probe.db",
            static _ => throw new NotSupportedException("the path format is not supported"),
            reports.Add);

        string report = Assert.Single(reports);

        Assert.Contains("probe.db", report, StringComparison.Ordinal);

        Assert.Contains("the path format is not supported", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sweep runs from the static constructor too. A failure of any kind, not only the I/O and access
    /// failures it expects, is reported and skipped instead of failing every later <c>Skip.IfNot</c> with a
    /// <see cref="TypeInitializationException"/>.
    /// </summary>
    [Fact]
    public void A_sweep_that_fails_for_any_reason_is_reported_and_never_escapes()
    {
        List<string> reports = [];

        int removed = GrimoireFixture.TrySweepAbandonedTestArtifacts(
            "arcanum-tests",
            TimeSpan.FromHours(12),
            static (_, _) => throw new InvalidOperationException("an unexpected failure"),
            reports.Add);

        Assert.Equal(0, removed);

        string report = Assert.Single(reports);

        Assert.Contains("an unexpected failure", report, StringComparison.Ordinal);
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
    public async Task Dispose_deletes_the_wal_and_shm_sidecars_of_every_copy()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireFixture scoped = new();

        string copyPath = scoped.CopyDatabase();

        ArcanumDbContext context = scoped.CreateContext(copyPath);

        Assert.True(await context.Database.CanConnectAsync());

        await context.DisposeAsync();

        await File.WriteAllTextAsync(copyPath + "-wal", "orphaned WAL marker");

        await File.WriteAllTextAsync(copyPath + "-shm", "orphaned SHM marker");

        Assert.True(File.Exists(copyPath + "-wal"));

        Assert.True(File.Exists(copyPath + "-shm"));

        scoped.Dispose();

        string[] suffixes = ["", ".kdf", "-wal", "-shm"];

        foreach (string suffix in suffixes)
        {
            Assert.False(File.Exists(copyPath + suffix), copyPath + suffix);
        }
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
