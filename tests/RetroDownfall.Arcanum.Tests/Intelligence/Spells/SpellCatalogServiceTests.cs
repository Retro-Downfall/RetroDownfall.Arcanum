using System.Text;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence.Spells;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence.Spells;

[Collection("ProcessEnvironment")]
public sealed class SpellCatalogServiceTests : IAsyncLifetime
{

    private readonly Dictionary<string, string?> _originalEnvironment = [];

    private TempWorkspace _workspace = null!;

    private string _testHome = string.Empty;

    public async Task InitializeAsync()
    {

        _testHome = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            $"spell-catalog-{Guid.NewGuid():N}");

        Directory.CreateDirectory(_testHome);

        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Testing");

        SetEnvironment("DOTNET_ENVIRONMENT", "Testing");

        SetEnvironment("ARCANUM_TEST_HOME", _testHome);

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

    }

    public async Task DisposeAsync()
    {

        await _workspace.DisposeAsync();

        foreach (KeyValuePair<string, string?> entry in _originalEnvironment)
        {

            System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);

        }

        if (Directory.Exists(_testHome))
        {

            Directory.Delete(_testHome, recursive: true);

        }

    }

    [Fact]
    public async Task PageAsync_streams_metadata_with_bounded_retention_and_opaque_continuation()
    {

        const int spellCount = 130;

        WriteSpells(spellCount);

        RecordingCatalogObserver observer = new();

        SpellCatalogService service = CreateService(observer);

        SpellCatalogPage first = AssertSuccess(
            await service.PageAsync(
                _workspace.Root,
                new SpellCatalogQuery(Source: SpellSource.Workspace),
                CancellationToken.None));

        Assert.Equal(50, first.Items.Length);

        Assert.Equal(
            Enumerable.Range(0, 50).Select(static index => $"spell-{index:D3}"),
            first.Items.Select(static item => item.Name));

        Assert.True(first.HasMore);

        Assert.NotNull(first.NextCursor);

        Assert.DoesNotContain("spell-049", first.NextCursor, StringComparison.Ordinal);

        Assert.InRange(observer.PeakRetainedCandidates, 1, 51);

        Assert.True(observer.ScannedCandidates >= spellCount);

        Assert.All(first.Items, static item => Assert.Null(item.Dependencies));

    }

    [Fact]
    public async Task PageAsync_continuation_is_stable_when_an_earlier_spell_is_inserted()
    {

        WriteSpells(80);

        SpellCatalogService service = CreateService();

        SpellCatalogPage first = AssertSuccess(
            await service.PageAsync(
                _workspace.Root,
                new SpellCatalogQuery(Source: SpellSource.Workspace),
                CancellationToken.None));

        string cursor = Assert.IsType<string>(first.NextCursor);

        WriteSpell("aaa-inserted", "inserted");

        SpellCatalogPage second = AssertSuccess(
            await service.PageAsync(
                _workspace.Root,
                new SpellCatalogQuery(
                    Source: SpellSource.Workspace,
                    Cursor: cursor),
                CancellationToken.None));

        Assert.Equal(
            Enumerable.Range(50, 30).Select(static index => $"spell-{index:D3}"),
            second.Items.Select(static item => item.Name));

    }

    [Fact]
    public async Task PageAsync_replays_a_repeated_cursor_but_returned_cursors_advance()
    {

        WriteSpells(130);

        SpellCatalogService service = CreateService();

        SpellCatalogPage first = AssertSuccess(
            await service.PageAsync(
                _workspace.Root,
                new SpellCatalogQuery(Source: SpellSource.Workspace),
                CancellationToken.None));

        string firstCursor = Assert.IsType<string>(first.NextCursor);

        SpellCatalogPage second = AssertSuccess(
            await service.PageAsync(
                _workspace.Root,
                new SpellCatalogQuery(
                    Source: SpellSource.Workspace,
                    Cursor: firstCursor),
                CancellationToken.None));

        SpellCatalogPage replay = AssertSuccess(
            await service.PageAsync(
                _workspace.Root,
                new SpellCatalogQuery(
                    Source: SpellSource.Workspace,
                    Cursor: firstCursor),
                CancellationToken.None));

        Assert.Equal(
            second.Items.Select(static spell => spell.Name),
            replay.Items.Select(static spell => spell.Name));

        Assert.Equal(second.NextCursor, replay.NextCursor);

        Assert.NotEqual(firstCursor, second.NextCursor);

    }

    [Fact]
    public async Task PageAsync_requests_restart_when_the_cursor_anchor_vanished()
    {

        WriteSpells(80);

        SpellCatalogService service = CreateService();

        SpellCatalogPage first = AssertSuccess(
            await service.PageAsync(
                _workspace.Root,
                new SpellCatalogQuery(Source: SpellSource.Workspace),
                CancellationToken.None));

        string cursor = Assert.IsType<string>(first.NextCursor);

        Directory.Delete(
            Path.Combine(_workspace.Root, "spells", "spell-049"),
            recursive: true);

        Result<SpellCatalogPage> second = await service.PageAsync(
            _workspace.Root,
            new SpellCatalogQuery(
                Source: SpellSource.Workspace,
                Cursor: cursor),
            CancellationToken.None);

        Assert.True(second.IsFailure);

        Assert.Equal("Spell.ContinuationCheckpointMissing", second.Error.Code);

        Assert.Contains("Restart", second.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task PageAsync_binds_the_cursor_to_search_arguments()
    {

        WriteSpells(80);

        SpellCatalogService service = CreateService();

        SpellCatalogPage first = AssertSuccess(
            await service.PageAsync(
                _workspace.Root,
                new SpellCatalogQuery(Source: SpellSource.Workspace),
                CancellationToken.None));

        Result<SpellCatalogPage> mismatch = await service.PageAsync(
            _workspace.Root,
            new SpellCatalogQuery(
                Query: "spell-07",
                Source: SpellSource.Workspace,
                Cursor: first.NextCursor),
            CancellationToken.None);

        Assert.True(mismatch.IsFailure);

        Assert.Equal("Spell.ContinuationQueryMismatch", mismatch.Error.Code);

    }

    [Fact]
    public async Task PageAsync_reads_only_frontmatter_not_the_full_spell_body()
    {

        string path = _workspace.WriteFile(
            "spells/metadata-only/SPELL.md",
            "---\nname: metadata-only\ndescription: safe metadata\n---\nbody");

        await using (FileStream stream = new(path, FileMode.Append, FileAccess.Write))
        {

            await stream.WriteAsync(new byte[] { 0xFF, 0xFE, 0xFA });

        }

        SpellCatalogPage page = AssertSuccess(
            await CreateService().PageAsync(
                _workspace.Root,
                new SpellCatalogQuery(Source: SpellSource.Workspace),
                CancellationToken.None));

        SpellSummary item = Assert.Single(page.Items);

        Assert.Equal("metadata-only", item.Name);

        Assert.Equal("safe metadata", item.Description);

    }

    [Fact]
    public async Task PageAsync_propagates_cancellation_during_large_catalog_scan()
    {

        WriteSpells(80);

        using CancellationTokenSource cancellation = new();

        CancellingCatalogObserver observer = new(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateService(observer).PageAsync(
                _workspace.Root,
                new SpellCatalogQuery(Source: SpellSource.Workspace),
                cancellation.Token));

        Assert.True(observer.ScannedCandidates >= 2);

    }

    [Fact]
    public async Task PageAsync_reports_an_actionable_physical_cursor_frame_boundary()
    {

        for (int index = 0; index < 49; index++)
        {

            WriteSpell(
                $"a-spell-{index:D3}",
                $"description {index:D3}");

        }

        string oversizedName = "m-" + new string('x', (64 * 1024) + 1);

        _workspace.WriteFile(
            "spells/oversized-name/SPELL.md",
            $"---\nname: {oversizedName}\ndescription: oversized cursor anchor\n---\nbody");

        WriteSpell("z-last", "last");

        Result<SpellCatalogPage> result = await CreateService().PageAsync(
            _workspace.Root,
            new SpellCatalogQuery(Source: SpellSource.Workspace),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(
            ErrorCodes.Spell.ContinuationFrameTooLarge,
            result.Error.Code);

        Assert.Contains("65,536 bytes", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("Server state was not changed", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("restart with cursor omitted", result.Error.Message, StringComparison.OrdinalIgnoreCase);

    }

    private SpellCatalogService CreateService(
        ISpellCatalogProgressObserver? observer = null) =>
        new(
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            observer);

    private static SpellCatalogPage AssertSuccess(
        Result<SpellCatalogPage> result)
    {

        Assert.True(result.IsSuccess, result.Error.Message);

        return Assert.IsType<SpellCatalogPage>(result.Value);

    }

    private void WriteSpells(int count)
    {

        for (int index = 0; index < count; index++)
        {

            WriteSpell(
                $"spell-{index:D3}",
                $"description {index:D3}");

        }

    }

    private void WriteSpell(string name, string description)
    {

        _workspace.WriteFile(
            $"spells/{name}/SPELL.md",
            $"---\nname: {name}\ndescription: {description}\ntags: [catalog]\ntools: [read_file]\n---\nbody {name}");

    }

    private void SetEnvironment(string name, string value)
    {

        _originalEnvironment[name] = System.Environment.GetEnvironmentVariable(name);

        System.Environment.SetEnvironmentVariable(name, value);

    }

    private sealed class RecordingCatalogObserver : ISpellCatalogProgressObserver
    {

        public int ScannedCandidates { get; private set; }

        public int PeakRetainedCandidates { get; private set; }

        public void OnCandidateScanned(int retainedCandidates)
        {

            ScannedCandidates++;

            PeakRetainedCandidates = Math.Max(
                PeakRetainedCandidates,
                retainedCandidates);

        }

    }

    private sealed class CancellingCatalogObserver(
        CancellationTokenSource cancellation) : ISpellCatalogProgressObserver
    {

        public int ScannedCandidates { get; private set; }

        public void OnCandidateScanned(int retainedCandidates)
        {

            _ = retainedCandidates;

            ScannedCandidates++;

            if (ScannedCandidates == 2)
            {

                cancellation.Cancel();

            }

        }

    }

}
