using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

[Collection("SpellScanner")]
public sealed class SpellDependencyResolverTests : IDisposable
{

    private readonly string _workspace;

    private readonly long _maxFileSizeBytes = 262144L;

    public SpellDependencyResolverTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "arcanum-resonance-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Dependency_chain_reaches_beyond_the_former_total_depth_ceiling()
    {
        await CreateSpellAsync("primary", "Primary", "primary body", ["SpellA"]);

        await CreateSpellAsync("spell-a", "SpellA", "a body", ["SpellB"]);

        await CreateSpellAsync("spell-b", "SpellB", "b body", ["SpellC"]);

        await CreateSpellAsync("spell-c", "SpellC", "c body", ["SpellD"]);

        await CreateSpellAsync("spell-d", "SpellD", "d body", dependencies: null);

        ParsedSpell? primary = await LoadSpellAsync("primary");

        Assert.NotNull(primary);

        ResolvedSpell resolved = await SpellDependencyResolver.ResolveAsync(
            primary!,
            _workspace,
            _maxFileSizeBytes,
            CancellationToken.None);

        Assert.Equal("Primary", resolved.Primary!.Name);

        Assert.Equal(["SpellA", "SpellB", "SpellC", "SpellD"], resolved.Resonants.Select(static s => s.Name));

    }

    [Fact]
    public async Task Cycle_DoesNotDuplicateResonants()
    {
        await CreateSpellAsync("spell-a", "SpellA", "a body", ["SpellB"]);

        await CreateSpellAsync("spell-b", "SpellB", "b body", ["SpellA"]);

        ParsedSpell? primary = await LoadSpellAsync("spell-a");

        Assert.NotNull(primary);

        ResolvedSpell resolved = await SpellDependencyResolver.ResolveAsync(
            primary!,
            _workspace,
            _maxFileSizeBytes,
            CancellationToken.None);

        Assert.Single(resolved.Resonants);

        Assert.Equal("SpellB", resolved.Resonants[0].Name);
    }

    [Fact]
    public async Task SelfDependency_IsIgnored()
    {
        await CreateSpellAsync("spell-a", "SpellA", "a body", ["SpellA"]);

        ParsedSpell? primary = await LoadSpellAsync("spell-a");

        Assert.NotNull(primary);

        ResolvedSpell resolved = await SpellDependencyResolver.ResolveAsync(
            primary!,
            _workspace,
            _maxFileSizeBytes,
            CancellationToken.None);

        Assert.Empty(resolved.Resonants);
    }

    [Fact]
    public async Task Diamond_DeduplicatesSharedDependency()
    {
        await CreateSpellAsync("primary", "Primary", "primary body", ["SpellA", "SpellB"]);

        await CreateSpellAsync("spell-a", "SpellA", "a body", ["SpellC"]);

        await CreateSpellAsync("spell-b", "SpellB", "b body", ["SpellC"]);

        await CreateSpellAsync("spell-c", "SpellC", "c body", dependencies: null);

        ParsedSpell? primary = await LoadSpellAsync("primary");

        Assert.NotNull(primary);

        ResolvedSpell resolved = await SpellDependencyResolver.ResolveAsync(
            primary!,
            _workspace,
            _maxFileSizeBytes,
            CancellationToken.None);

        Assert.Equal(3, resolved.Resonants.Count);

        Assert.Single(resolved.Resonants, static s => s.Name == "SpellC");

        Assert.True(resolved.DependencyEdges.ContainsKey("SpellA"));

        Assert.True(resolved.DependencyEdges.ContainsKey("SpellB"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("absent")]
    public async Task EmptyNullOrAbsentDependencies_YieldZeroResonants(string mode)
    {
        await CreateSpellAsync("primary", "Primary", "primary body", dependencies: null, dependencyMode: mode);

        ParsedSpell? primary = await LoadSpellAsync("primary");

        Assert.NotNull(primary);

        ResolvedSpell resolved = await SpellDependencyResolver.ResolveAsync(
            primary!,
            _workspace,
            _maxFileSizeBytes,
            CancellationToken.None);

        Assert.Empty(resolved.Resonants);
    }

    [Fact]
    public async Task MissingDependency_IsSkippedWithoutThrow()
    {
        await CreateSpellAsync("primary", "Primary", "primary body", ["MissingSpell"]);

        ParsedSpell? primary = await LoadSpellAsync("primary");

        Assert.NotNull(primary);

        ResolvedSpell resolved = await SpellDependencyResolver.ResolveAsync(
            primary!,
            _workspace,
            _maxFileSizeBytes,
            CancellationToken.None,
            NullLogger.Instance);

        Assert.Empty(resolved.Resonants);
    }

    private async Task<ParsedSpell?> LoadSpellAsync(string folderName)
    {
        string path = Path.Combine(_workspace, folderName, "SPELL.md");

        return await SpellScanner.LoadFullAsync(path, CancellationToken.None, _maxFileSizeBytes);
    }

    private async Task CreateSpellAsync(
        string folderName,
        string spellName,
        string body,
        string[]? dependencies,
        string dependencyMode = "list")
    {
        string dir = Path.Combine(_workspace, folderName);

        Directory.CreateDirectory(dir);

        string spellMd = $"---\nname: {spellName}\ndescription: test\n---\n{body}\n";

        await File.WriteAllTextAsync(Path.Combine(dir, "SPELL.md"), spellMd);

        if (dependencyMode == "absent")
        {
            return;
        }

        string dependenciesJson = dependencyMode switch
        {
            "null" => "null",
            _ => JsonSerializer.Serialize(dependencies ?? Array.Empty<string>()),
        };

        string skillJson = $$"""

            {
              "name": "{{spellName}}",
              "version": "1.0.0",
              "description": "test",
              "tags": [],
              "declaredTools": [],
              "dependencies": {{dependenciesJson}}
            }

            """.Trim();

        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.json"), skillJson);
    }

}
