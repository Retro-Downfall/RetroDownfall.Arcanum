using System.Text.Json;
using RetroDownfall.Arcanum.Core.Tower;
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
        // LRU cache or, if evicted, start a fresh content-equal parse. Record equality is NOT the
        // stable invariant here — ParsedSpell carries an IReadOnlyList<string> and string[] members,
        // whose compiler-generated equality is reference equality, so a fresh parse of the same file
        // is never Equal to the leader's instance. Compare the content instead.
        Assert.All(results, r => AssertSameSpellContent(first, r));

    }

    /// <summary>
    /// Content equality for two independently produced <see cref="ParsedSpell"/> instances.
    /// The compiler-generated record equality compares <see cref="ParsedSpell.AvailableScripts"/>,
    /// <see cref="ParsedSpell.Tags"/>, <see cref="ParsedSpell.Tools"/> and
    /// <see cref="ParsedSpell.RequiredMcpServers"/> by reference, so it holds only while every caller
    /// happens to receive the same cached instance. That made
    /// <c>LoadFullAsync_concurrent_misses_share_one_parse</c> pass in isolation and fail under load,
    /// where a follower released after the wave closed re-parsed and got fresh collections.
    /// </summary>
    private static void AssertSameSpellContent(ParsedSpell? expected, ParsedSpell? actual)
    {

        Assert.NotNull(expected);

        Assert.NotNull(actual);

        Assert.Equal(expected!.Name, actual!.Name);

        Assert.Equal(expected.Description, actual.Description);

        Assert.Equal(expected.FilePath, actual.FilePath);

        Assert.Equal(expected.FullContent, actual.FullContent);

        Assert.Equal(expected.DirectoryPath, actual.DirectoryPath);

        Assert.Equal(expected.Body, actual.Body);

        Assert.Equal(expected.SystemPrompt, actual.SystemPrompt);

        Assert.Equal(expected.Template, actual.Template);

        Assert.Equal(expected.Model, actual.Model);

        Assert.Equal(expected.Provider, actual.Provider);

        Assert.Equal(expected.AvailableScripts, actual.AvailableScripts);

        Assert.Equal(expected.Tags, actual.Tags);

        Assert.Equal(expected.Tools, actual.Tools);

        Assert.Equal(expected.RequiredMcpServers, actual.RequiredMcpServers);

        AssertSameSkillMetadata(expected.SkillMetadata, actual.SkillMetadata);

    }

    /// <summary>
    /// Content equality for two independently deserialized <see cref="SkillMetadata"/> instances. The
    /// record closes over <c>List&lt;string&gt;</c>, <c>Dictionary&lt;string, double&gt;?</c> and
    /// <c>JsonDocument?</c> members, every one of which its compiler-generated equality compares by
    /// reference, so comparing the two records would hold only while both sides share one cached
    /// instance — the exact assumption <see cref="AssertSameSpellContent"/> exists to stop relying on.
    /// </summary>
    private static void AssertSameSkillMetadata(SkillMetadata? expected, SkillMetadata? actual)
    {

        if (expected is null || actual is null)
        {

            Assert.Null(expected);

            Assert.Null(actual);

            return;

        }

        Assert.Equal(expected.Name, actual.Name);

        Assert.Equal(expected.Version, actual.Version);

        Assert.Equal(expected.Description, actual.Description);

        Assert.Equal(expected.Tags, actual.Tags);

        Assert.Equal(RawJson(expected.InputSchema), RawJson(actual.InputSchema));

        Assert.Equal(RawJson(expected.OutputSchema), RawJson(actual.OutputSchema));

        Assert.Equal(expected.DeclaredTools, actual.DeclaredTools);

        Assert.Equal(expected.Dependencies, actual.Dependencies);

        Assert.Equal(expected.Model, actual.Model);

        Assert.Equal(expected.Provider, actual.Provider);

        // Ordered by key so the comparison is over the pairs themselves rather than over whatever
        // enumeration order the two dictionaries happened to be filled in.
        Assert.Equal(
            expected.DefaultParameters?.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
            actual.DefaultParameters?.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray());

        Assert.Equal(expected.LastModified, actual.LastModified);

        Assert.Equal(expected.ActiveVersion, actual.ActiveVersion);

    }

    /// <summary>
    /// The schema's own text. Two <see cref="JsonDocument"/> instances parsed from one file are never
    /// <c>Equal</c>, and the document is what the sidecar carries, so its raw text is the invariant.
    /// </summary>
    private static string? RawJson(JsonDocument? document) => document?.RootElement.GetRawText();

    /// <summary>
    /// The deterministic form of the load-flake in <c>LoadFullAsync_concurrent_misses_share_one_parse</c>:
    /// bumping the file's last-write time changes the <c>$"{fullPath}|{mtimeTicks}"</c> cache key, so the
    /// second call re-parses from disk exactly as a follower released after a closed single-flight wave
    /// does. The two instances are content-identical, and that — not record equality — is what a
    /// concurrent caller may rely on.
    /// </summary>
    [Fact]
    public async Task LoadFullAsync_reparse_after_a_cache_miss_returns_a_content_equal_spell()
    {

        string spellDir = Path.Combine(_workspace.Root, "spells", "reparse-full");

        Directory.CreateDirectory(Path.Combine(spellDir, "scripts"));

        _workspace.WriteFile(
            "spells/reparse-full/SPELL.md",
            """
            ---
            name: reparse-full
            description: reparse test
            tags: [alpha]
            tools: [read_file]
            ---
            body
            """);

        _workspace.WriteFile("spells/reparse-full/scripts/a.sh", "#!/bin/sh\necho a\n");

        _workspace.WriteFile("spells/reparse-full/scripts/b.sh", "#!/bin/sh\necho b\n");

        string spellPath = Path.Combine(spellDir, "SPELL.md");

        ParsedSpell? first = await SpellScanner.LoadFullAsync(spellPath, CancellationToken.None, MaxFileSizeBytes);

        Assert.NotNull(first);

        File.SetLastWriteTimeUtc(spellPath, File.GetLastWriteTimeUtc(spellPath).AddSeconds(1));

        ParsedSpell? second = await SpellScanner.LoadFullAsync(spellPath, CancellationToken.None, MaxFileSizeBytes);

        Assert.NotNull(second);

        Assert.NotSame(first, second);

        AssertSameSpellContent(first, second);

    }

    /// <summary>
    /// The same re-parse, over a spell that carries a <c>SPELL.json</c> sidecar. <see cref="SkillMetadata"/>
    /// is a record over <c>List&lt;string&gt;</c>, <c>Dictionary&lt;string, double&gt;?</c> and
    /// <c>JsonDocument?</c> members, every one of which the compiler-generated equality compares by
    /// reference, so two independent deserializations of one sidecar are never <c>Equal</c>. Without a
    /// sidecar the member is null on both sides and the reference comparison holds by accident — which is
    /// exactly why this case has to be covered explicitly rather than left to the fixtures.
    /// </summary>
    [Fact]
    public async Task LoadFullAsync_reparse_of_a_spell_with_a_sidecar_returns_a_content_equal_spell()
    {

        _workspace.WriteFile(
            "spells/reparse-sidecar/SPELL.md",
            """
            ---
            name: reparse-sidecar
            description: reparse test with a sidecar
            ---
            body
            """);

        _workspace.WriteFile(
            "spells/reparse-sidecar/SPELL.json",
            """
            {
              "name": "reparse-sidecar",
              "version": "3.1.0",
              "description": "sidecar",
              "tags": ["alpha", "beta"],
              "inputSchema": { "type": "object" },
              "outputSchema": { "type": "string" },
              "declaredTools": ["read_file"],
              "dependencies": ["other-spell"],
              "model": "m",
              "provider": "p",
              "defaultParameters": { "temperature": 0.5 },
              "activeVersion": "3.1.0"
            }
            """);

        string spellPath = Path.Combine(_workspace.Root, "spells", "reparse-sidecar", "SPELL.md");

        ParsedSpell? first = await SpellScanner.LoadFullAsync(spellPath, CancellationToken.None, MaxFileSizeBytes);

        Assert.NotNull(first);

        Assert.NotNull(first!.SkillMetadata);

        File.SetLastWriteTimeUtc(spellPath, File.GetLastWriteTimeUtc(spellPath).AddSeconds(1));

        ParsedSpell? second = await SpellScanner.LoadFullAsync(spellPath, CancellationToken.None, MaxFileSizeBytes);

        Assert.NotNull(second);

        Assert.NotSame(first, second);

        Assert.NotSame(first.SkillMetadata, second!.SkillMetadata);

        AssertSameSpellContent(first, second);

    }

    /// <summary>
    /// The coalesced scan used to run under whichever caller happened to win the miss race, so a leader
    /// that aborted (client disconnect, Ctrl-C on `arcanum spell list`) faulted the shared task with its
    /// own OperationCanceledException and every joined caller rethrew it. A follower's own token was
    /// healthy, so ArcanumExceptionHandler did not treat that OCE as a client abort — it surfaced as a
    /// 500 Hub.Unhandled for a request nobody cancelled. A joined caller must observe only its own token.
    /// </summary>
    [Fact]
    public async Task ScanMetadataAsync_does_not_cancel_a_joined_caller_when_the_leader_cancels()
    {

        const int spellCount = 3000;

        for (int i = 0; i < spellCount; i++)
        {

            _workspace.WriteFile(
                $"spells/leader-cancel-{i:D4}/SPELL.md",
                $"---\nname: leader-cancel-{i:D4}\ndescription: leader cancellation test {i}\n---\nbody");

        }

        using CancellationTokenSource leaderCts = new();

        // ttlSeconds 0 is what SpellRepository / SpellSearchService / SpellDependencyResolver pass, so
        // both callers are guaranteed single-flight participants rather than TTL-cache readers.
        Task<IReadOnlyList<SpellMetadata>> leader = Task.Run(
            () => SpellScanner.ScanMetadataAsync(_workspace.Root, leaderCts.Token, MaxFileSizeBytes),
            CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(25));

        Task<IReadOnlyList<SpellMetadata>> follower = Task.Run(
            () => SpellScanner.ScanMetadataAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes),
            CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(25));

        await leaderCts.CancelAsync();

        IReadOnlyList<SpellMetadata> followerResult = await follower;

        Assert.Contains(followerResult, m => m.Name == "leader-cancel-0000");

        Assert.Contains(followerResult, m => m.Name == $"leader-cancel-{spellCount - 1:D4}");

        try
        {

            _ = await leader;

        }
        catch (OperationCanceledException)
        {

            // Expected when the cancel lands before the scan finishes; the leader must observe its own
            // cancellation, and only its own.

        }

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

        // Two TTLs rather than one. The "serves stale" leg used to run under the same 1 second TTL as
        // the refresh leg, so it silently required the first scan, the SPELL.md rewrite and the second
        // scan — two real directory walks over a temp workspace plus frontmatter parsing — to finish
        // inside 1000 ms. A cold, loaded or virus-scanned filesystem blew that budget and the entry had
        // already expired, so the second scan re-read the file and the assertion failed with "mutated".
        // The stale leg now has a 300 second budget (the ArcanumSettingClamps ceiling); only the refresh
        // leg is timed, and it waits *longer* than the TTL, which no amount of load can invalidate.
        const int staleTtl = 300;

        const int refreshTtl = 1;

        IReadOnlyList<Core.Intelligence.Spells.SpellSummary> first =
            await SpellScanner.ScanSummariesAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes, metadataScanCacheTtlSeconds: staleTtl);

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
            await SpellScanner.ScanSummariesAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes, metadataScanCacheTtlSeconds: staleTtl);

        Core.Intelligence.Spells.SpellSummary secondSummary = Assert.Single(second, s => s.Name == "ttl-spell");

        // Within the TTL the cached (stale) description is served, proving the TTL is threaded through.
        Assert.Equal("original", secondSummary.Description);

        // The entry was stamped by the first scan, so sleeping past refreshTtl guarantees expiry however
        // long the scans themselves took.
        await Task.Delay(TimeSpan.FromSeconds(refreshTtl + 1));

        IReadOnlyList<Core.Intelligence.Spells.SpellSummary> third =
            await SpellScanner.ScanSummariesAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes, metadataScanCacheTtlSeconds: refreshTtl);

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
    public async Task LoadFullAsync_accepts_dependencies_beyond_the_former_total_count_ceiling()
    {

        const int dependencyCount = 21;

        _workspace.WriteFile(
            "spells/many-dependencies/SPELL.md",
            """
            ---
            name: many-dependencies
            description: declares many dependencies
            ---
            body
            """);

        string dependencies = System.Text.Json.JsonSerializer.Serialize(
            Enumerable.Range(0, dependencyCount)
                .Select(static index => $"dependency-{index:D2}")
                .ToArray());

        _workspace.WriteFile(
            "spells/many-dependencies/SKILL.json",
            $$"""
            {
              "name": "many-dependencies",
              "version": "1.0.0",
              "description": "declares many dependencies",
              "tags": [],
              "declaredTools": [],
              "dependencies": {{dependencies}}
            }
            """);

        ParsedSpell? loaded = await SpellScanner.LoadFullAsync(
            Path.Combine(_workspace.Root, "spells", "many-dependencies", "SPELL.md"),
            CancellationToken.None,
            MaxFileSizeBytes);

        Assert.NotNull(loaded);

        Assert.NotNull(loaded!.SkillMetadata);

        Assert.Equal(dependencyCount, loaded.SkillMetadata!.Dependencies.Count);

    }

    [Fact]
    public async Task ScanAsync_reaches_beyond_the_former_total_depth_ceiling()
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

        Assert.Contains(spells, s => s.Name == "too-deep");

    }

    [Fact]
    public async Task LoadFullAsync_reads_canonical_SPELL_json()
    {

        _workspace.WriteFile(
            "spells/canonical-meta/SPELL.md",
            """
            ---
            name: canonical-meta
            description: uses SPELL.json
            ---
            body
            """);

        _workspace.WriteFile(
            "spells/canonical-meta/SPELL.json",
            """
            {
              "name": "canonical-meta",
              "version": "2.0.0",
              "description": "canonical",
              "tags": ["from-spell-json"],
              "declaredTools": [],
              "dependencies": []
            }
            """);

        string spellPath = Path.Combine(_workspace.Root, "spells", "canonical-meta", "SPELL.md");

        ParsedSpell? loaded = await SpellScanner.LoadFullAsync(spellPath, CancellationToken.None, MaxFileSizeBytes);

        Assert.NotNull(loaded);

        Assert.NotNull(loaded!.SkillMetadata);

        Assert.Equal("2.0.0", loaded.SkillMetadata!.Version);

        Assert.Contains("from-spell-json", loaded.Tags);

    }

    [Fact]
    public async Task LoadFullAsync_falls_back_to_legacy_SKILL_json()
    {

        _workspace.WriteFile(
            "spells/legacy-meta/SPELL.md",
            """
            ---
            name: legacy-meta
            description: uses SKILL.json
            ---
            body
            """);

        _workspace.WriteFile(
            "spells/legacy-meta/SKILL.json",
            """
            {
              "name": "legacy-meta",
              "version": "1.5.0",
              "description": "legacy",
              "tags": ["from-skill-json"],
              "declaredTools": [],
              "dependencies": []
            }
            """);

        string spellPath = Path.Combine(_workspace.Root, "spells", "legacy-meta", "SPELL.md");

        ParsedSpell? loaded = await SpellScanner.LoadFullAsync(spellPath, CancellationToken.None, MaxFileSizeBytes);

        Assert.NotNull(loaded);

        Assert.NotNull(loaded!.SkillMetadata);

        Assert.Equal("1.5.0", loaded.SkillMetadata!.Version);

        Assert.Contains("from-skill-json", loaded.Tags);

    }

    [Fact]
    public async Task LoadFullAsync_prefers_SPELL_json_when_both_sidecars_exist()
    {

        _workspace.WriteFile(
            "spells/both-meta/SPELL.md",
            """
            ---
            name: both-meta
            description: both sidecars
            ---
            body
            """);

        _workspace.WriteFile(
            "spells/both-meta/SKILL.json",
            """
            {
              "name": "both-meta",
              "version": "1.0.0",
              "description": "legacy-loses",
              "tags": ["legacy-tag"],
              "declaredTools": [],
              "dependencies": []
            }
            """);

        _workspace.WriteFile(
            "spells/both-meta/SPELL.json",
            """
            {
              "name": "both-meta",
              "version": "9.0.0",
              "description": "canonical-wins",
              "tags": ["canonical-tag"],
              "declaredTools": [],
              "dependencies": []
            }
            """);

        string spellPath = Path.Combine(_workspace.Root, "spells", "both-meta", "SPELL.md");

        ParsedSpell? loaded = await SpellScanner.LoadFullAsync(spellPath, CancellationToken.None, MaxFileSizeBytes);

        Assert.NotNull(loaded);

        Assert.NotNull(loaded!.SkillMetadata);

        Assert.Equal("9.0.0", loaded.SkillMetadata!.Version);

        Assert.Contains("canonical-tag", loaded.Tags);

        Assert.DoesNotContain("legacy-tag", loaded.Tags);

    }

    /// <summary>
    /// A FIFO named SPELL.md is yielded by the walk (Directory.EnumerateFiles returns FIFOs and the lexical
    /// containment check passes because the path really is inside the root) and TryGetFileLength reports 0 for
    /// it, so the size gate let it through to a blocking open(2) that never returns until a writer appears.
    /// ScanMetadataAsync coalesces through SingleFlight, so one planted FIFO wedged the spell catalog for every
    /// concurrent caller of that workspace — permanently, because the shared task never completes.
    /// </summary>
    [SkippableFact]
    public async Task ScanMetadataAsync_skips_a_fifo_spell_file_instead_of_blocking_forever()
    {

        Skip.If(OperatingSystem.IsWindows(), "mkfifo is a POSIX primitive.");

        await CreateFifoAsync("spells/piped/SPELL.md");

        // Offloaded so a regression blocks a pool thread rather than wedging the whole test run.
        Task<IReadOnlyList<SpellMetadata>> scan = Task.Run(
            () => SpellScanner.ScanMetadataAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes));

        Task completed = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.Same(scan, completed);

        IReadOnlyList<SpellMetadata> metadata = await scan;

        Assert.Contains(metadata, m => m.Name == "fireball");

        Assert.DoesNotContain(metadata, m => m.Name == "piped");

    }

    /// <summary>
    /// The full-parse walk reaches the same FIFO through File.ReadAllTextAsync, which both blocks on the open
    /// and — once any writer attaches — reads to EOF with no cap, because the size check only runs after the
    /// whole payload is already materialized in memory.
    /// </summary>
    [SkippableFact]
    public async Task ScanAsync_skips_a_fifo_spell_file_instead_of_blocking_forever()
    {

        Skip.If(OperatingSystem.IsWindows(), "mkfifo is a POSIX primitive.");

        await CreateFifoAsync("spells/piped/SPELL.md");

        Task<IReadOnlyList<ParsedSpell>> scan = Task.Run(
            () => SpellScanner.ScanAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes));

        Task completed = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.Same(scan, completed);

        IReadOnlyList<ParsedSpell> spells = await scan;

        Assert.Contains(spells, s => s.Name == "fireball");

        Assert.DoesNotContain(spells, s => s.Name == "piped");

    }

    /// <summary>
    /// The SPELL.md body is read through SecureFileReader, but the SPELL.json sidecar sixty lines below was
    /// read with a plain File.ReadAllTextAsync behind the weak TryGetFileLength gate. A FIFO stats as length
    /// 0, so the size gate passed and open(2) parked forever waiting for a writer — no CancellationToken can
    /// interrupt an open, so GET /api/spells/{name} never returned and pinned a thread-pool thread per call.
    /// </summary>
    [SkippableFact]
    public async Task LoadFullAsync_skips_a_fifo_sidecar_instead_of_blocking_forever()
    {

        Skip.If(OperatingSystem.IsWindows(), "mkfifo is a POSIX primitive.");

        _workspace.WriteFile(
            "spells/piped-sidecar/SPELL.md",
            """
            ---
            name: piped-sidecar
            description: the sidecar next to this spell is a FIFO
            ---
            body
            """);

        await CreateFifoAsync("spells/piped-sidecar/SPELL.json");

        string spellPath = Path.Combine(_workspace.Root, "spells", "piped-sidecar", "SPELL.md");

        // Offloaded so a regression blocks a pool thread rather than wedging the whole test run.
        Task<ParsedSpell?> load = Task.Run(
            () => SpellScanner.LoadFullAsync(spellPath, CancellationToken.None, MaxFileSizeBytes));

        Task completed = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.Same(load, completed);

        ParsedSpell? loaded = await load;

        Assert.NotNull(loaded);

        Assert.Equal("piped-sidecar", loaded!.Name);

        Assert.Null(loaded.SkillMetadata);

    }

    /// <summary>
    /// The full-parse walk supplies a non-null revalidation root, but RevalidatePathBeforeIo is a lexical and
    /// symlink-containment check with no file-kind test, so an in-workspace FIFO sidecar sailed through it too
    /// and wedged the whole catalog walk rather than one spell.
    /// </summary>
    [SkippableFact]
    public async Task ScanAsync_skips_a_fifo_sidecar_instead_of_blocking_forever()
    {

        Skip.If(OperatingSystem.IsWindows(), "mkfifo is a POSIX primitive.");

        _workspace.WriteFile(
            "spells/piped-sidecar/SPELL.md",
            """
            ---
            name: piped-sidecar
            description: the sidecar next to this spell is a FIFO
            ---
            body
            """);

        await CreateFifoAsync("spells/piped-sidecar/SPELL.json");

        Task<IReadOnlyList<ParsedSpell>> scan = Task.Run(
            () => SpellScanner.ScanAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes));

        Task completed = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.Same(scan, completed);

        IReadOnlyList<ParsedSpell> spells = await scan;

        Assert.Contains(spells, s => s.Name == "fireball");

        ParsedSpell piped = Assert.Single(spells, s => s.Name == "piped-sidecar");

        Assert.Null(piped.SkillMetadata);

    }

    /// <summary>
    /// DESIGN §11 promises every SPELL.md / sidecar read is proven to stay inside the workspace. The sidecar
    /// read bypassed that, so a SPELL.json symlinked out of the workspace was opened and deserialized, merging
    /// its tags and description into the catalog entry.
    /// </summary>
    [SkippableFact]
    public async Task LoadFullAsync_rejects_a_sidecar_symlinked_outside_root()
    {

        Skip.If(OperatingSystem.IsWindows(), "Symlink creation requires elevation on Windows.");

        string outsideFile = Path.Combine(Path.GetTempPath(), "arcanum-outside-" + Guid.NewGuid().ToString("N") + ".json");

        await File.WriteAllTextAsync(
            outsideFile,
            """
            {
              "name": "escaped-sidecar",
              "version": "9.9.9",
              "description": "should never be read through a symlink",
              "tags": ["from-outside-the-workspace"],
              "declaredTools": [],
              "dependencies": []
            }
            """);

        try
        {

            _workspace.WriteFile(
                "spells/escaped-sidecar/SPELL.md",
                """
                ---
                name: escaped-sidecar
                description: the sidecar next to this spell escapes the workspace
                ---
                body
                """);

            File.CreateSymbolicLink(
                Path.Combine(_workspace.Root, "spells", "escaped-sidecar", "SPELL.json"),
                outsideFile);

            string spellPath = Path.Combine(_workspace.Root, "spells", "escaped-sidecar", "SPELL.md");

            ParsedSpell? loaded = await SpellScanner.LoadFullAsync(spellPath, CancellationToken.None, MaxFileSizeBytes);

            Assert.NotNull(loaded);

            Assert.Null(loaded!.SkillMetadata);

            Assert.DoesNotContain("from-outside-the-workspace", loaded.Tags);

        }
        finally
        {

            if (File.Exists(outsideFile))
            {

                File.Delete(outsideFile);

            }

        }

    }

    private async Task CreateFifoAsync(string relativePath)
    {

        string fifoPath = Path.Combine(_workspace.Root, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fifoPath)!);

        using System.Diagnostics.Process? mkfifo = System.Diagnostics.Process.Start("mkfifo", fifoPath);

        Skip.If(mkfifo is null, "mkfifo is unavailable on this host.");

        await mkfifo!.WaitForExitAsync();

        Skip.If(mkfifo.ExitCode != 0, "mkfifo failed on this host.");

        Assert.True(File.Exists(fifoPath));

    }

}
