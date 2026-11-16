using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DiagnosticsProcess = System.Diagnostics.Process;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Backup;
using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Packaging;

[Collection("WorkspacePathPolicy")]
public sealed class PublishedArcanumBackupIntegrityTests(ITestOutputHelper output)
{
    private const string SourceSession = "66666666-6666-4666-8666-666666666666";

    private const string SourceEntry = "77777777-7777-4777-8777-777777777777";

    private const string PassphraseVariable = "ARCANUM_NATIVE_BACKUP_TEST_PASSPHRASE";

    [SkippableTheory]
    [InlineData("'not-an-integer'", false)]
    [InlineData("1.5", false)]
    [InlineData("X'01'", false)]
    [InlineData("4294967296", true)]
    public async Task Published_selective_import_requires_integer_sequences_and_preserves_Int64(string sequenceSql, bool valid)
    {
        string executable = global::System.Environment.GetEnvironmentVariable("ARCANUM_PUBLISHED_EXECUTABLE") ?? string.Empty;

        Skip.If(string.IsNullOrEmpty(executable), "Set ARCANUM_PUBLISHED_EXECUTABLE to the exact Native AOT apphost.");

        Assert.True(File.Exists(executable));

        string binaryHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable)));

        PublishedTestProcessIsolation isolation = PublishedTestProcessIsolation.Create("backup-integrity");

        try
        {
            isolation.Prepare();

            using BackupRestoreServiceTests sourceOwner = new();

            BackupRestoreServiceTests.Fixture source = await sourceOwner.CreateFixtureAsync();

            using TcpListener reservation = new(IPAddress.Loopback, 0);

            reservation.Start();

            int isolatedPort = ((IPEndPoint)reservation.LocalEndpoint).Port;

            Assert.NotEqual(5001, isolatedPort);

            reservation.Stop();

            string profile = Path.Combine(isolation.TestHome, ".config", "arcanum");

            Directory.CreateDirectory(profile);

            string initialConfiguration = IsolatedConfiguration(isolatedPort, covenant: false);

            // Both the initial command and the configuration it restores name the same
            // private port; no startup fallback can address the normal port 5001 host.
            await File.WriteAllTextAsync(Path.Combine(profile, "arcanum.json"), initialConfiguration);

            await File.WriteAllTextAsync(Path.Combine(sourceOwner.Installation, "arcanum.json"), initialConfiguration);

            await using (SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
                source.DatabasePath, source.GrimoireSecret, readOnly: false, CancellationToken.None))
            {
                using CovenantSqliteAuthorizationScope authorization = CovenantSqliteConnectionInitializer.Instance
                    .Authorize(connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);

                await using SqliteCommand seed = connection.CreateCommand();

                seed.CommandText = $"""
                    INSERT INTO Sessions(Id,Title,Status,CreatedAt,UpdatedAt)
                    VALUES('{SourceSession}','Synthetic native import','active','2026-11-16T00:00:00.0000000Z','2026-11-16T00:00:00.0000000Z');
                    INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence)
                    VALUES('{SourceEntry}','{SourceSession}',0,'Synthetic archive entry','test','2026-11-16T00:00:00.0000000Z',1);
                    """;

                await seed.ExecuteNonQueryAsync();
            }

            // The native full restore initializes only this private destination and persists
            // its DP-protected Grimoire secret. This step starts no host or inference provider.
            string initialArchive = await source.CreateBackupAsync("native-initial.arcbackup");

            using NativeRestoreResult initialized = await RunRestoreAsync(executable, isolation, initialArchive,
                ["--conflict-mode", "replace-installation"], expectedExitCode: 0);

            Assert.Equal("Completed", initialized.Document.RootElement.GetProperty("status").GetString());

            // The archive fixture intentionally carries an old, minimal configuration. Select
            // the protected import arm explicitly, without touching any process-global paths.
            await File.WriteAllTextAsync(Path.Combine(profile, "arcanum.json"),
                IsolatedConfiguration(isolatedPort, covenant: true));

            await InitializeInstallationAuthorityAsync(executable, isolation, isolatedPort);

            string destinationDatabase = Path.Combine(profile, "arcanum.db");

            long sessionsBefore = await ScalarAsync(destinationDatabase, source.GrimoireSecret, "SELECT count(*) FROM Sessions");

            long entriesBefore = await ScalarAsync(destinationDatabase, source.GrimoireSecret, "SELECT count(*) FROM Entries");

            long journalBefore = await ScalarAsync(destinationDatabase, source.GrimoireSecret, "SELECT count(*) FROM protected_session_transfer_intents");

            await using (SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
                source.DatabasePath, source.GrimoireSecret, readOnly: false, CancellationToken.None))
            await using (SqliteCommand corruptOrWiden = connection.CreateCommand())
            {
                corruptOrWiden.CommandText = $"UPDATE Entries SET Sequence={sequenceSql} WHERE Id='{SourceEntry}'";

                await corruptOrWiden.ExecuteNonQueryAsync();
            }

            string selectedArchive = await source.CreateBackupAsync("native-selected.arcbackup");

            // Accept either exit here so the old apphost's committed malformed import is
            // observed in the actual encrypted database before the expected refusal fails.
            using NativeRestoreResult imported = await RunRestoreAsync(executable, isolation, selectedArchive,
                ["--conflict-mode", "import-selected-sessions", "--session-id", SourceSession], expectedExitCode: null);

            string issueCodes = string.Join(",", imported.Document.RootElement.GetProperty("issues")
                .EnumerateArray().Select(static issue => issue.GetProperty("code").GetString()));

            string blockerCodes = string.Join(",", imported.Document.RootElement.GetProperty("plan").GetProperty("blockers")
                .EnumerateArray().Select(static issue => issue.GetProperty("code").GetString()));

            string issueMessages = string.Join("; ", imported.Document.RootElement.GetProperty("issues")
                .EnumerateArray().Select(static issue => issue.GetProperty("message").GetString()))
                .Replace(source.GrimoireSecret, "[synthetic key]", StringComparison.Ordinal)
                .Replace(BackupRestoreServiceTests.Passphrase, "[synthetic passphrase]", StringComparison.Ordinal)
                .Replace(sourceOwner.Installation, "[source fixture]", StringComparison.Ordinal)
                .Replace(isolation.TestHome, "[isolated home]", StringComparison.Ordinal);

            output.WriteLine($"Native selective import: exit={imported.ExitCode}; status={imported.Document.RootElement.GetProperty("status").GetString()}; issues={issueCodes}; blockers={blockerCodes}; reasons={issueMessages}");

            long sessionsAfter = await ScalarAsync(destinationDatabase, source.GrimoireSecret, "SELECT count(*) FROM Sessions");

            long entriesAfter = await ScalarAsync(destinationDatabase, source.GrimoireSecret, "SELECT count(*) FROM Entries");

            if (valid)
            {
                Assert.Equal(0, imported.ExitCode);

                Assert.Equal("Completed", imported.Document.RootElement.GetProperty("status").GetString());

                Assert.Equal(sessionsBefore + 1, sessionsAfter);

                Assert.Equal(entriesBefore + 1, entriesAfter);

                Assert.Equal(1L, await ScalarAsync(destinationDatabase, source.GrimoireSecret,
                    "SELECT count(*) FROM Entries WHERE Sequence=4294967296 AND typeof(Sequence)='integer'"));
            }
            else
            {
                Assert.Equal(sessionsBefore, sessionsAfter);

                Assert.Equal(entriesBefore, entriesAfter);

                Assert.Equal(journalBefore, await ScalarAsync(destinationDatabase, source.GrimoireSecret,
                    "SELECT count(*) FROM protected_session_transfer_intents"));

                Assert.Equal(1, imported.ExitCode);

                Assert.NotEqual("Completed", imported.Document.RootElement.GetProperty("status").GetString());

                Assert.Contains(imported.Document.RootElement.GetProperty("issues").EnumerateArray(),
                    static issue => issue.GetProperty("code").GetString() == "backup.restore_import_refused"
                        && issue.GetProperty("message").GetString() == "An archived Entry has a non-integer sequence.");
            }
        }
        finally
        {
            try
            {
                Assert.Equal(binaryHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable))));
            }
            finally
            {
                isolation.Delete();
            }
        }
    }

    private sealed record NativeRestoreResult(int ExitCode, JsonDocument Document) : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }

    private static async Task<long> ScalarAsync(string database, string secret, string sql)
    {
        await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            database, secret, readOnly: true, CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return Assert.IsType<long>(await command.ExecuteScalarAsync());
    }

    private static string IsolatedConfiguration(int port, bool covenant) =>
        "{\"Arcanum\":{\"Providers\":[],\"Features\":{\"Covenant\":" + (covenant ? "true" : "false")
        + ",\"Apprentices\":false,\"Lexicon\":false,\"ArchiveSearch\":false,\"Metrics\":false,\"Scrying\":false,"
        + "\"Attachments\":false,\"Reasoning\":false,\"WorkspaceChecks\":false},\"Host\":{\"Port\":" + port + "}}}";

    private static async Task InitializeInstallationAuthorityAsync(string executable, PublishedTestProcessIsolation isolation, int port)
    {
        using DiagnosticsProcess host = DiagnosticsProcess.Start(CreateStartInfo(executable, isolation, ["serve", "--plain"]))
            ?? throw new InvalidOperationException("The isolated installation bootstrap did not start.");

        Task<string> stdout = host.StandardOutput.ReadToEndAsync();

        Task<string> stderr = host.StandardError.ReadToEndAsync();

        try
        {
            using CancellationTokenSource watchdog = new(TimeSpan.FromMinutes(2));

            bool listening = false;

            while (!host.HasExited)
            {
                using TcpClient probe = new();

                try
                {
                    await probe.ConnectAsync(IPAddress.Loopback, port, watchdog.Token);

                    listening = true;

                    break;
                }
                catch (SocketException)
                {
                    await Task.Delay(20, watchdog.Token);
                }
            }

            Assert.True(listening, "The isolated host exited before establishing installation authority and its private listener.");

            using NativeRestoreResult quit = await RunCommandAsync(executable, isolation,
                ["serve", "quit", "--json", "--no-context"], expectedExitCode: 0);

            await host.WaitForExitAsync(watchdog.Token);

            Assert.Equal(0, host.ExitCode);
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);

                await host.WaitForExitAsync();
            }

            await Task.WhenAll(stdout, stderr);
        }
    }

    private static Task<NativeRestoreResult> RunRestoreAsync(string executable, PublishedTestProcessIsolation isolation,
        string archive, string[] options, int? expectedExitCode) =>
        RunCommandAsync(executable, isolation,
            ["backup", "restore", archive, "--passphrase-env", PassphraseVariable,
                "--no-safety-backup", "--yes", "--json", "--no-context", .. options], expectedExitCode);

    private static ProcessStartInfo CreateStartInfo(string executable, PublishedTestProcessIsolation isolation, IEnumerable<string> arguments)
    {
        ProcessStartInfo start = new(executable)
        {
            WorkingDirectory = isolation.TestHome,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        string? path = global::System.Environment.GetEnvironmentVariable("PATH");

        string? systemRoot = global::System.Environment.GetEnvironmentVariable("SystemRoot");

        string? comSpec = global::System.Environment.GetEnvironmentVariable("ComSpec");

        start.Environment.Clear();

        if (path is not null) start.Environment["PATH"] = path;

        if (systemRoot is not null) start.Environment["SystemRoot"] = systemRoot;

        if (comSpec is not null) start.Environment["ComSpec"] = comSpec;

        start.Environment["DOTNET_ENVIRONMENT"] = "Testing";

        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";

        start.Environment["ARCANUM_TEST_HOME"] = isolation.TestHome;

        start.Environment["ARCANUM_TEST_IN_MEMORY_CREDENTIALS"] = "1";

        start.Environment["ARCANUM_NO_AUTO_SERVE"] = "1";

        start.Environment["NO_COLOR"] = "1";

        start.Environment["HOME"] = isolation.TestHome;

        start.Environment["USERPROFILE"] = isolation.TestHome;

        start.Environment["APPDATA"] = Path.Combine(isolation.TestHome, "AppData", "Roaming");

        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(isolation.TestHome, ".config");

        start.Environment["XDG_DATA_HOME"] = Path.Combine(isolation.TestHome, ".local", "share");

        start.Environment["XDG_CACHE_HOME"] = Path.Combine(isolation.TestHome, ".cache");

        start.Environment["DOTNET_CLI_HOME"] = Path.Combine(isolation.TestHome, ".dotnet");

        start.Environment[PassphraseVariable] = BackupRestoreServiceTests.Passphrase;

        isolation.ApplyTemporaryEnvironment(start);

        Assert.True(RetroDownfall.Arcanum.Infrastructure.Security.TestCredentialStorePolicy.IsEnabled(
            "Testing", isolation.TestHome, "1", isolation.ProcessTemporaryDirectory));

        return start;
    }

    private static async Task<NativeRestoreResult> RunCommandAsync(string executable, PublishedTestProcessIsolation isolation,
        string[] arguments, int? expectedExitCode)
    {
        ProcessStartInfo start = CreateStartInfo(executable, isolation, arguments);

        using DiagnosticsProcess process = DiagnosticsProcess.Start(start) ?? throw new InvalidOperationException("The isolated native restore did not start.");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();

        Task<string> stderr = process.StandardError.ReadToEndAsync();

        try
        {
            using CancellationTokenSource watchdog = new(TimeSpan.FromMinutes(3));

            await process.WaitForExitAsync(watchdog.Token);

            string output = await stdout;

            _ = await stderr;

            if (expectedExitCode is { } expected) Assert.Equal(expected, process.ExitCode);

            Assert.True(process.ExitCode is 0 or 1, "The isolated restore refused its invocation before reaching the backup operation.");

            return new(process.ExitCode, JsonDocument.Parse(output));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);

                await process.WaitForExitAsync();
            }

            await Task.WhenAll(stdout, stderr);
        }
    }
}
