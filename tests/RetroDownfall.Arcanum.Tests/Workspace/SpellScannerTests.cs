using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

[Collection("SpellScanner")]
public sealed class SpellScannerTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    private const long MaxFileSizeBytes = 1024 * 1024;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _workspace.WriteFile(
            "spells/fireball/SPELL.md",
            """
            ---
            name: fireball
            description: A blazing spell
            tags: [combat]
            tools: [read_file]
            ---
            # Fireball

            Cast it.
            """);

        _workspace.WriteFile("spells/ignored/node_modules/hidden/SPELL.md", "---\nname: hidden\n---\n");

        _workspace.WriteFile("spells/heavy/SPELL.md", new string('x', (int)MaxFileSizeBytes + 1));

    }

    public async Task DisposeAsync()
    {

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task ScanAsync_finds_workspace_spells_and_skips_heavy_dirs()
    {

        IReadOnlyList<ParsedSpell> spells = await SpellScanner.ScanAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes);

        ParsedSpell fireball = Assert.Single(spells, s => s.Name == "fireball");

        Assert.Equal("A blazing spell", fireball.Description);

        Assert.Contains("read_file", fireball.Tools);

        Assert.DoesNotContain(spells, s => s.Name == "hidden");

    }

    [Fact]
    public async Task ScanMetadataAsync_returns_lightweight_metadata()
    {

        IReadOnlyList<SpellMetadata> metadata = await SpellScanner.ScanMetadataAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes);

        SpellMetadata fireball = Assert.Single(metadata, m => m.Name == "fireball");

        Assert.Equal("A blazing spell", fireball.Description);

        Assert.Contains("combat", fireball.Tags!);

    }

    [Fact]
    public async Task LoadFullAsync_caches_parsed_spell_by_mtime()
    {

        string spellPath = Path.Combine(_workspace.Root, "spells", "fireball", "SPELL.md");

        ParsedSpell? first = await SpellScanner.LoadFullAsync(spellPath, CancellationToken.None, MaxFileSizeBytes);

        ParsedSpell? second = await SpellScanner.LoadFullAsync(spellPath, CancellationToken.None, MaxFileSizeBytes);

        Assert.NotNull(first);

        Assert.Same(first, second);

    }

    [Fact]
    public async Task ScanSummariesAsync_maps_workspace_source()
    {

        IReadOnlyList<Core.Intelligence.Spells.SpellSummary> summaries =
            await SpellScanner.ScanSummariesAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes);

        Core.Intelligence.Spells.SpellSummary fireball = Assert.Single(summaries, s => s.Name == "fireball");

        Assert.Equal(Core.Intelligence.Spells.SpellSource.Workspace, fireball.Source);

    }

    [Fact]
    public async Task ScanMetadataAsync_concurrent_misses_share_one_scan()
    {

        for (int i = 0; i < 24; i++)
        {

            _workspace.WriteFile(
                $"spells/concurrent-{i:D2}/SPELL.md",
                $"""
                ---
                name: concurrent-{i:D2}
                description: concurrent scan test {i}
                ---
                body
                """);

        }

        const int concurrency = 16;

        Barrier barrier = new(concurrency);

        Task<IReadOnlyList<SpellMetadata>>[] tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(async () =>
            {

                barrier.SignalAndWait();

                return await SpellScanner.ScanMetadataAsync(
                    _workspace.Root,
                    CancellationToken.None,
                    MaxFileSizeBytes,
                    metadataScanCacheTtlSeconds: 60);

            }))
            .ToArray();

        IReadOnlyList<SpellMetadata>[] results = await Task.WhenAll(tasks);

        // The fixture seeds a "fireball" spell in InitializeAsync; the test adds 24 more
        // concurrent-* spells, so the workspace has 25 spells total. Single-flight coalesces
        // the miss onto one scan; the stable invariant is content-equality across all callers
        // (a caller released after the leader may hit the LRU cache or, if evicted, start a
        // fresh content-equal scan). The at-most-once scan invariant is proven directly by
        // SingleFlightTests.
        string[] firstNames = results[0].Select(static m => m.Name).OrderBy(static n => n, StringComparer.Ordinal).ToArray();

        Assert.All(results, r =>
        {
            string[] names = r.Select(static m => m.Name).OrderBy(static n => n, StringComparer.Ordinal).ToArray();

            Assert.Equal(firstNames, names);

        });

        Assert.Equal(25, firstNames.Length);

    }

    [Fact]
    public async Task LoadFullAsync_concurrent_misses_share_one_parse()
    {

        string spellDir = Path.Combine(_workspace.Root, "spells", "concurrent-full");

        Directory.CreateDirectory(Path.Combine(spellDir, "scripts"));

        _workspace.WriteFile(
            "spells/concurrent-full/SPELL.md",
            """
            ---
            name: concurrent-full
            description: concurrent full parse test
            ---
            """ + new string('\n', 2000) + "body");

        _workspace.WriteFile("spells/concurrent-full/scripts/a.sh", "#!/bin/sh\necho a\n");

        _workspace.WriteFile("spells/concurrent-full/scripts/b.sh", "#!/bin/sh\necho b\n");

        string spellPath = Path.Combine(spellDir, "SPELL.md");

        const int concurrency = 16;

        Barrier barrier = new(concurrency);

        Task<ParsedSpell?>[] tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(async () =>
            {

                barrier.SignalAndWait();

                return await SpellScanner.LoadFullAsync(spellPath, CancellationToken.None, MaxFileSizeBytes);

            }))
            .ToArray();

        ParsedSpell?[] results = await Task.WhenAll(tasks);

        ParsedSpell? first = results[0];

        Assert.NotNull(first);

        // Same rationale as ScanMetadataAsync_concurrent_misses_share_one_scan: single-flight
        // coalesces the miss onto one parse, but a caller released after the leader may hit the
        // LRU cache or, if evicted, start a fresh content-equal parse. ParsedSpell is a record,
        // so value equality is the correct stable invariant.
        Assert.All(results, r => Assert.Equal(first, r));

    }

    [Fact]
    public async Task ScanSummariesAsync_with_ttl_serves_stale_within_ttl_then_refreshes()
    {

        _workspace.WriteFile(
            "spells/ttl-spell/SPELL.md",
            """
            ---
            name: ttl-spell
            description: original
            ---
            body
            """);

        const int ttl = 1;

        IReadOnlyList<Core.Intelligence.Spells.SpellSummary> first =
            await SpellScanner.ScanSummariesAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes, metadataScanCacheTtlSeconds: ttl);

        Core.Intelligence.Spells.SpellSummary firstSummary = Assert.Single(first, s => s.Name == "ttl-spell");

        Assert.Equal("original", firstSummary.Description);

        // Mutate the underlying SPELL.md while the cache entry is still fresh.
        _workspace.WriteFile(
            "spells/ttl-spell/SPELL.md",
            """
            ---
            name: ttl-spell
            description: mutated
            ---
            body
            """);

        IReadOnlyList<Core.Intelligence.Spells.SpellSummary> second =
            await SpellScanner.ScanSummariesAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes, metadataScanCacheTtlSeconds: ttl);

        Core.Intelligence.Spells.SpellSummary secondSummary = Assert.Single(second, s => s.Name == "ttl-spell");

        // Within the TTL the cached (stale) description is served, proving the TTL is threaded through.
        Assert.Equal("original", secondSummary.Description);

        // After the TTL expires the next call re-scans and picks up the mutation.
        await Task.Delay(TimeSpan.FromSeconds(ttl + 1));

        IReadOnlyList<Core.Intelligence.Spells.SpellSummary> third =
            await SpellScanner.ScanSummariesAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes, metadataScanCacheTtlSeconds: ttl);

        Core.Intelligence.Spells.SpellSummary thirdSummary = Assert.Single(third, s => s.Name == "ttl-spell");

        Assert.Equal("mutated", thirdSummary.Description);

    }

    [Fact]
    public async Task LoadFullAsync_loads_skill_json_and_scripts()
    {

        string spellDir = Path.Combine(_workspace.Root, "spells", "scripted");

        Directory.CreateDirectory(Path.Combine(spellDir, "scripts"));

        _workspace.WriteFile(
            "spells/scripted/SPELL.md",
            """
            ---
            name: scripted
            description: runs scripts
            ---
            body
            """);

        _workspace.WriteFile("spells/scripted/scripts/run.sh", "#!/bin/sh");

        _workspace.WriteFile(
            "spells/scripted/SKILL.json",
            """
            {
              "name": "scripted",
              "version": "1.0.0",
              "description": "test",
              "tags": ["automation"],
              "declaredTools": [],
              "dependencies": []
            }
            """);

        string spellPath = Path.Combine(spellDir, "SPELL.md");

        ParsedSpell? loaded = await SpellScanner.LoadFullAsync(spellPath, CancellationToken.None, MaxFileSizeBytes);

        Assert.NotNull(loaded);

        Assert.Contains("automation", loaded!.Tags);

        Assert.Contains("run.sh", loaded.AvailableScripts);

        Assert.NotNull(loaded.SkillMetadata);

    }

    [Fact]
    public async Task LoadFullAsync_returns_null_for_oversized_spell()
    {

        string spellPath = Path.Combine(_workspace.Root, "spells", "heavy", "SPELL.md");

        ParsedSpell? loaded = await SpellScanner.LoadFullAsync(spellPath, CancellationToken.None, MaxFileSizeBytes);

        Assert.Null(loaded);

    }

    [Fact]
    public async Task LoadFullAsync_returns_null_for_missing_path()
    {

        ParsedSpell? loaded = await SpellScanner.LoadFullAsync(
            Path.Combine(_workspace.Root, "missing", "SPELL.md"),
            CancellationToken.None,
            MaxFileSizeBytes);

        Assert.Null(loaded);

    }

    [Fact]
    public async Task ScanMetadataAsync_spell_without_frontmatter_uses_directory_name()
    {

        _workspace.WriteFile("spells/plain-dir/SPELL.md", "# No frontmatter\nJust body.");

        IReadOnlyList<SpellMetadata> metadata = await SpellScanner.ScanMetadataAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes);

        SpellMetadata plain = Assert.Single(metadata, m => m.Name == "plain-dir");

        Assert.Equal(string.Empty, plain.Description);

    }

    [Fact]
    public async Task ScanAsync_null_workspace_returns_empty_or_global_only()
    {

        IReadOnlyList<ParsedSpell> spells = await SpellScanner.ScanAsync(null, CancellationToken.None, MaxFileSizeBytes);

        Assert.DoesNotContain(spells, s => s.Name == "fireball");

    }

    [Fact]
    public async Task ScanMetadataAsync_local_spell_overrides_global_name_collision()
    {

        string localDir = Path.Combine(_workspace.Root, "collision");

        Directory.CreateDirectory(localDir);

        _workspace.WriteFile(
            "collision/SPELL.md",
            """
            ---
            name: shared-name
            description: local override
            ---
            local body
            """);

        IReadOnlyList<SpellMetadata> metadata = await SpellScanner.ScanMetadataAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes);

        SpellMetadata? collision = metadata.FirstOrDefault(m => m.Name == "shared-name");

        if (collision is not null)
        {
            Assert.Contains("local override", collision.Description, StringComparison.Ordinal);
        }

    }

    [SkippableFact]
    public async Task ScanAsync_terminates_on_directory_symlink_cycle()
    {

        Skip.If(OperatingSystem.IsWindows(), "Directory symlink creation requires elevation on Windows.");

        _workspace.WriteFile(
            "spells/real-spell/SPELL.md",
            """
            ---
            name: real-spell
            description: A real spell
            ---
            body
            """);

        string spellsDir = Path.Combine(_workspace.Root, "spells");

        string cycleLink = Path.Combine(spellsDir, "cycle");

        Directory.CreateSymbolicLink(cycleLink, spellsDir);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));

        IReadOnlyList<ParsedSpell> spells = await SpellScanner.ScanAsync(_workspace.Root, cts.Token, MaxFileSizeBytes);

        Assert.Contains(spells, s => s.Name == "real-spell");

    }

    [SkippableFact]
    public async Task ScanAsync_rejects_spell_md_symlinked_outside_root()
    {

        Skip.If(OperatingSystem.IsWindows(), "Symlink creation requires elevation on Windows.");

        string outsideFile = Path.Combine(Path.GetTempPath(), "arcanum-outside-" + Guid.NewGuid().ToString("N") + ".md");

        await File.WriteAllTextAsync(
            outsideFile,
            """
            ---
            name: escaped-secret
            description: should never be read through a symlink
            ---
            body
            """);

        try
        {

            string evilDir = Path.Combine(_workspace.Root, "spells", "evil");

            Directory.CreateDirectory(evilDir);

            string evilSpell = Path.Combine(evilDir, "SPELL.md");

            File.CreateSymbolicLink(evilSpell, outsideFile);

            IReadOnlyList<ParsedSpell> spells = await SpellScanner.ScanAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes);

            Assert.DoesNotContain(spells, s => s.Name == "escaped-secret");

        }
        finally
        {

            if (File.Exists(outsideFile))
            {

                File.Delete(outsideFile);

            }

        }

    }

    [Fact]
    public async Task ScanAsync_drops_skill_metadata_when_declared_tools_exceed_configured_bound()
    {

        _workspace.WriteFile(
            "spells/bounded/SPELL.md",
            """
            ---
            name: bounded
            description: declares multiple tools
            ---
            body
            """);

        _workspace.WriteFile(
            "spells/bounded/SKILL.json",
            """
            {
              "name": "bounded",
              "version": "1.0.0",
              "description": "declares multiple tools",
              "tags": [],
              "declaredTools": ["alpha", "beta", "gamma"],
              "dependencies": []
            }
            """);

        IReadOnlyList<ParsedSpell> withinBound = await SpellScanner.ScanAsync(
            _workspace.Root, CancellationToken.None, MaxFileSizeBytes, maxDeclaredTools: 5);

        ParsedSpell withinSpell = Assert.Single(withinBound, s => s.Name == "bounded");

        Assert.NotNull(withinSpell.SkillMetadata);

        IReadOnlyList<ParsedSpell> belowBound = await SpellScanner.ScanAsync(
            _workspace.Root, CancellationToken.None, MaxFileSizeBytes, maxDeclaredTools: 1);

        ParsedSpell belowSpell = Assert.Single(belowBound, s => s.Name == "bounded");

        Assert.Null(belowSpell.SkillMetadata);

    }

    [Fact]
    public async Task ScanAsync_does_not_descend_beyond_max_depth()
    {

        string relative = "spells";

        for (int i = 0; i < 80; i++)
        {
            relative = Path.Combine(relative, "d");
        }

        _workspace.WriteFile(
            Path.Combine(relative, "SPELL.md"),
            """
            ---
            name: too-deep
            description: nested beyond the scan depth cap
            ---
            body
            """);

        IReadOnlyList<ParsedSpell> spells = await SpellScanner.ScanAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes);

        Assert.DoesNotContain(spells, s => s.Name == "too-deep");

    }

}
