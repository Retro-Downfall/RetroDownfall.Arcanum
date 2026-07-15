using System.Text.Json;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Intelligence.Spells;

public sealed class SkillJsonIOTests
{

    [Fact]
    public void HasStructuredFields_detects_create_request_fields()
    {

        CreateSpellRequest empty = new(
            "n", null, [], null, null, null, null, [], []);

        Assert.False(SkillJsonIO.HasStructuredFields(empty));

        using JsonDocument schema = JsonDocument.Parse("{\"type\":\"object\"}");

        CreateSpellRequest structured = empty with
        {
            Version = "2.0.0",
            InputSchema = schema,
        };

        Assert.True(SkillJsonIO.HasStructuredFields(structured));

    }

    [Fact]
    public void BuildMetadataFromCreate_applies_defaults()
    {

        CreateSpellRequest request = new(
            "n",
            "desc",
            ["tag"],
            null,
            null,
            "model",
            "provider",
            [],
            []);

        SkillMetadata metadata = SkillJsonIO.BuildMetadataFromCreate("spell-a", request);

        Assert.Equal("spell-a", metadata.Name);

        Assert.Equal("1.0.0", metadata.Version);

        Assert.Equal("desc", metadata.Description);

        Assert.Equal(["tag"], metadata.Tags);

    }

    [Fact]
    public void MergeMetadata_prefers_update_values_and_existing_fallbacks()
    {

        SkillMetadata existingMeta = new(
            "old",
            "1.0.0",
            "old-desc",
            ["t"],
            null,
            null,
            [],
            [],
            "old-model",
            "old-provider",
            null,
            DateTimeOffset.UtcNow);

        ParsedSpell existing = new(
            "spell",
            "old-desc",
            "/tmp/spell/SKILL.md",
            "content",
            "/tmp/spell",
            [])
        {
            Tags = ["t"],
            Model = "old-model",
            Provider = "old-provider",
            SkillMetadata = existingMeta,
        };

        UpdateSpellRequest update = new(
            Description: "new-desc",
            Tags: null,
            SystemPrompt: null,
            Template: null,
            Model: "new-model",
            Provider: null,
            Tools: null,
            RequiredMcpServers: null,
            Version: "3.0.0");

        SkillMetadata merged = SkillJsonIO.MergeMetadata(existing, update);

        Assert.Equal("3.0.0", merged.Version);

        Assert.Equal("new-desc", merged.Description);

        Assert.Equal("new-model", merged.Model);

        Assert.Equal("old-provider", merged.Provider);

        Assert.Equal(["t"], merged.Tags);

    }

    [Fact]
    public void HasStructuredFields_detects_update_request_fields()
    {

        UpdateSpellRequest empty = new(
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        Assert.False(SkillJsonIO.HasStructuredFields(empty));

        using JsonDocument schema = JsonDocument.Parse("{\"type\":\"object\"}");

        UpdateSpellRequest structured = empty with
        {
            OutputSchema = schema,
            DeclaredTools = [],
        };

        Assert.True(SkillJsonIO.HasStructuredFields(structured));

    }

    [Fact]
    public void MergeMetadata_without_existing_skill_metadata_uses_spell_fields()
    {

        ParsedSpell existing = new(
            "spell",
            "desc",
            "/tmp/spell/SKILL.md",
            "content",
            "/tmp/spell",
            ["tool-a"])
        {
            Tags = ["tag-a"],
            Model = "model-a",
            Provider = "provider-a",
        };

        UpdateSpellRequest update = new(
            Description: null,
            Tags: ["tag-b"],
            SystemPrompt: null,
            Template: null,
            Model: null,
            Provider: "provider-b",
            Tools: null,
            RequiredMcpServers: null,
            Version: null);

        SkillMetadata merged = SkillJsonIO.MergeMetadata(existing, update);

        Assert.Equal("desc", merged.Description);

        Assert.Equal(["tag-b"], merged.Tags);

        Assert.Equal("model-a", merged.Model);

        Assert.Equal("provider-b", merged.Provider);

    }

    [Fact]
    public void ResolveSidecarPath_prefers_canonical_SPELL_json()
    {

        string dir = Path.Combine(Path.GetTempPath(), "arcanum-sidecar-resolve", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(dir);

        try
        {

            File.WriteAllText(Path.Combine(dir, SkillJsonIO.LegacyFileName), "{\"name\":\"legacy\"}");

            File.WriteAllText(Path.Combine(dir, SkillJsonIO.CanonicalFileName), "{\"name\":\"canonical\"}");

            string? path = SkillJsonIO.ResolveSidecarPath(dir);

            Assert.NotNull(path);

            Assert.Equal(SkillJsonIO.CanonicalFileName, Path.GetFileName(path));

        }
        finally
        {

            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

        }

    }

    [Fact]
    public void ResolveSidecarPath_falls_back_to_legacy_SKILL_json()
    {

        string dir = Path.Combine(Path.GetTempPath(), "arcanum-sidecar-legacy", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(dir);

        try
        {

            File.WriteAllText(Path.Combine(dir, SkillJsonIO.LegacyFileName), "{\"name\":\"legacy\"}");

            string? path = SkillJsonIO.ResolveSidecarPath(dir);

            Assert.NotNull(path);

            Assert.Equal(SkillJsonIO.LegacyFileName, Path.GetFileName(path));

        }
        finally
        {

            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

        }

    }

    [Fact]
    public void ResolveSidecarPath_returns_null_when_neither_exists()
    {

        string dir = Path.Combine(Path.GetTempPath(), "arcanum-sidecar-none", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(dir);

        try
        {

            Assert.Null(SkillJsonIO.ResolveSidecarPath(dir));

        }
        finally
        {

            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

        }

    }

    [Fact]
    public async Task WriteAsync_writes_canonical_SPELL_json_file()
    {

        string dir = Path.Combine(Path.GetTempPath(), "arcanum-spell-json", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(dir);

        try
        {

            SkillMetadata metadata = new(
                "file-spell",
                "1.0.0",
                null,
                [],
                null,
                null,
                [],
                [],
                null,
                null,
                null,
                DateTimeOffset.UtcNow);

            await SkillJsonIO.WriteAsync(dir, metadata, CancellationToken.None);

            string canonical = Path.Combine(dir, SkillJsonIO.CanonicalFileName);

            string legacy = Path.Combine(dir, SkillJsonIO.LegacyFileName);

            Assert.True(File.Exists(canonical));

            Assert.False(File.Exists(legacy));

            string json = await File.ReadAllTextAsync(canonical);

            Assert.Contains("file-spell", json, StringComparison.Ordinal);

        }
        finally
        {

            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

        }

    }

    [Fact]
    public async Task WriteAsync_after_legacy_only_spell_creates_canonical_without_removing_legacy()
    {

        string dir = Path.Combine(Path.GetTempPath(), "arcanum-sidecar-migrate", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(dir);

        try
        {

            await File.WriteAllTextAsync(
                Path.Combine(dir, SkillJsonIO.LegacyFileName),
                """{"name":"legacy-spell","version":"1.0.0","tags":[],"declaredTools":[],"dependencies":[]}""");

            SkillMetadata metadata = new(
                "legacy-spell",
                "2.0.0",
                null,
                [],
                null,
                null,
                [],
                [],
                null,
                null,
                null,
                DateTimeOffset.UtcNow);

            await SkillJsonIO.WriteAsync(dir, metadata, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(dir, SkillJsonIO.CanonicalFileName)));

            Assert.True(File.Exists(Path.Combine(dir, SkillJsonIO.LegacyFileName)));

            Assert.Equal(
                Path.Combine(dir, SkillJsonIO.CanonicalFileName),
                SkillJsonIO.ResolveSidecarPath(dir));

        }
        finally
        {

            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

        }

    }

    [Fact]
    public void MergeMetadata_preserves_active_version()
    {

        SkillMetadata existingMeta = new(
            "old",
            "1.0.0",
            "old-desc",
            ["t"],
            null,
            null,
            [],
            [],
            "old-model",
            "old-provider",
            null,
            DateTimeOffset.UtcNow,
            "v1");

        ParsedSpell existing = new(
            "spell",
            "old-desc",
            "/tmp/spell/SPELL.md",
            "content",
            "/tmp/spell",
            [])
        {
            Tags = ["t"],
            Model = "old-model",
            Provider = "old-provider",
            SkillMetadata = existingMeta,
        };

        UpdateSpellRequest update = new(
            Description: "new-desc",
            Tags: null,
            SystemPrompt: null,
            Template: null,
            Model: null,
            Provider: null,
            Tools: null,
            RequiredMcpServers: null,
            Version: "3.0.0");

        SkillMetadata merged = SkillJsonIO.MergeMetadata(existing, update);

        Assert.Equal("v1", merged.ActiveVersion);

        Assert.Equal("3.0.0", merged.Version);

    }

    [SkippableFact]
    public async Task WriteAsync_overwrites_atomically_and_leaves_no_temp_files()
    {

        Skip.If(OperatingSystem.IsWindows(), "Atomic replace is verified via POSIX open-handle snapshot semantics.");

        string dir = Path.Combine(Path.GetTempPath(), "arcanum-spell-json-atomic", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(dir);

        string path = Path.Combine(dir, SkillJsonIO.CanonicalFileName);

        try
        {

            SkillMetadata original = new(
                "original-spell",
                "1.0.0",
                "first",
                [],
                null,
                null,
                [],
                [],
                null,
                null,
                null,
                DateTimeOffset.UtcNow);

            await SkillJsonIO.WriteAsync(dir, original, CancellationToken.None);

            // Hold a read handle open across the overwrite. An in-place truncate would mutate the
            // bytes this handle sees; an atomic temp-file + rename preserves the original inode.
            await using FileStream openHandle = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            SkillMetadata replacement = new(
                "replacement-spell",
                "2.0.0",
                "second",
                [],
                null,
                null,
                [],
                [],
                null,
                null,
                null,
                DateTimeOffset.UtcNow);

            await SkillJsonIO.WriteAsync(dir, replacement, CancellationToken.None);

            using StreamReader reader = new(openHandle);

            string snapshot = await reader.ReadToEndAsync();

            Assert.Contains("original-spell", snapshot, StringComparison.Ordinal);

            Assert.DoesNotContain("replacement-spell", snapshot, StringComparison.Ordinal);

            string onDisk = await File.ReadAllTextAsync(path);

            Assert.Contains("replacement-spell", onDisk, StringComparison.Ordinal);

            string[] remaining = Directory.GetFiles(dir);

            Assert.Single(remaining);

            Assert.EndsWith(SkillJsonIO.CanonicalFileName, remaining[0], StringComparison.Ordinal);

        }
        finally
        {

            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

        }

    }

}
