using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// The one checked <c>wal_checkpoint(TRUNCATE)</c>: it reads the pragma's answer rather than assuming
/// it, and it is the only source of that statement a proof may rest on.
/// </summary>
[Collection("Grimoire")]
public sealed class GrimoireWalCheckpointTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private const string Pragma = "wal_checkpoint(TRUNCATE)";

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private static CancellationToken Token => CancellationToken.None;

    public Task InitializeAsync()
    {
        _dbPath = fixture.CopyDatabase();

        _db = fixture.CreateContext(_dbPath);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [SkippableFact]
    public async Task A_quiet_checkpoint_is_truncated()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await connection.OpenAsync(Token);

        await using (SqliteCommand write = connection.CreateCommand())
        {
            // A committed write, so there is a frame for the checkpoint to move.
            write.CommandText = "CREATE TABLE wal_checkpoint_probe (Value INTEGER); INSERT INTO wal_checkpoint_probe VALUES (1);";

            _ = await write.ExecuteNonQueryAsync(Token);
        }

        Result<CovenantWalCheckpointOutcome> outcome = await GrimoireWalCheckpoint.TruncateAsync(connection, Token);

        Assert.True(outcome.IsSuccess, outcome.IsFailure ? outcome.Error.Message : string.Empty);

        Assert.Equal(0, outcome.Value.Busy);

        Assert.True(outcome.Value.IsTruncated);

        Assert.True(outcome.Value.RequireTruncated().IsSuccess);
    }

    [Theory]
    [InlineData(0, 0, 4, true)]
    [InlineData(0, -1, -1, true)]
    [InlineData(1, 0, 4, false)]
    [InlineData(0, 3, 7, false)]
    public void Only_a_checkpoint_neither_busy_nor_partial_is_truncated(int busy, int remaining, int checkpointed, bool expected)
    {
        Assert.Equal(expected, new CovenantWalCheckpointOutcome(busy, remaining, checkpointed).IsTruncated);
    }

    /// <summary>
    /// Every string literal in production code that runs the truncating checkpoint, found through
    /// Roslyn so a comment that names the pragma is not mistaken for code that runs it.
    /// </summary>
    [Fact]
    public void The_checked_truncate_pragma_has_one_source()
    {
        string[] expected =
        [
            "src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireWalCheckpoint.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteNativeRuntimeValidator.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs",
        ];

        Assert.Equal(expected, FilesWithPragmaLiteral());
    }

    [Fact]
    public void The_pragma_scan_reads_literals_and_ignores_comments()
    {
        const string source = """
            internal static class Fixture
            {
                // PRAGMA wal_checkpoint(TRUNCATE); is named here and runs nowhere.
                /// <summary>Runs <c>PRAGMA wal_checkpoint(TRUNCATE)</c>.</summary>
                internal const string Statement = "PRAGMA wal_checkpoint(TRUNCATE);";
            }
            """;

        Assert.Equal(1, PragmaLiterals(source));

        Assert.Equal(0, PragmaLiterals("// PRAGMA wal_checkpoint(TRUNCATE);\ninternal static class Fixture { }"));
    }

    private static string[] FilesWithPragmaLiteral()
    {
        string repositoryRoot = NativeSqlCipherTestPaths.RepositoryRoot();

        List<string> files = [];

        foreach (string file in Directory.EnumerateFiles(Path.Combine(repositoryRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            if (PragmaLiterals(File.ReadAllText(file)) > 0)
            {
                files.Add(Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/'));
            }
        }

        return [.. files.Order(StringComparer.Ordinal)];
    }

    private static int PragmaLiterals(string source) =>
        CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))
            .GetRoot()
            .DescendantTokens()
            .Count(static token =>
                (token.IsKind(SyntaxKind.StringLiteralToken)
                    || token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
                    || token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken))
                && token.ValueText.Contains(Pragma, StringComparison.OrdinalIgnoreCase));
}
