using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

public sealed partial class SpellScannerTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hydration_advertises_scripts_through_an_inside_root_directory_link(bool fullScan)
    {
        string spellPath = Path.Combine(_workspace.Root, "spells", "fireball", "SPELL.md");

        string scripts = Path.Combine(Path.GetDirectoryName(spellPath)!, "scripts");

        string shared = Path.Combine(_workspace.Root, "shared-scripts");

        Directory.CreateDirectory(shared);

        await File.WriteAllTextAsync(Path.Combine(shared, "shared.sh"), "safe shared script");

        bool linked = false;

        try
        {
            try
            {
                Directory.CreateSymbolicLink(scripts, shared);

                linked = true;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                Skip.If(true, "This host does not permit directory symbolic links.");
            }

            ParsedSpell? parsed = fullScan
                ? Assert.Single(await SpellScanner.ScanAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes), spell => spell.Name == "fireball")
                : await SpellScanner.LoadFullAsync(spellPath, _workspace.Root, CancellationToken.None, MaxFileSizeBytes);

            Assert.NotNull(parsed);

            Assert.Equal("shared.sh", Assert.Single(parsed.AvailableScripts));
        }
        finally
        {
            if (linked)
            {
                Directory.Delete(scripts);
            }
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hydration_does_not_advertise_names_from_external_scripts_directory(bool fullScan)
    {
        string spellPath = Path.Combine(_workspace.Root, "spells", "fireball", "SPELL.md");

        string scripts = Path.Combine(Path.GetDirectoryName(spellPath)!, "scripts");

        string external = Path.Combine(Path.GetTempPath(), "spell-script-names-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(external);

        await File.WriteAllTextAsync(Path.Combine(external, "CONFIDENTIAL_EXTERNAL_FILENAME.sh"), "external content");

        bool linked = false;

        try
        {
            try
            {
                Directory.CreateSymbolicLink(scripts, external);

                linked = true;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                Skip.If(true, "This host does not permit directory symbolic links.");
            }

            ParsedSpell? parsed = fullScan
                ? Assert.Single(await SpellScanner.ScanAsync(_workspace.Root, CancellationToken.None, MaxFileSizeBytes), spell => spell.Name == "fireball")
                : await SpellScanner.LoadFullAsync(spellPath, _workspace.Root, CancellationToken.None, MaxFileSizeBytes);

            Assert.NotNull(parsed);

            Assert.DoesNotContain("CONFIDENTIAL_EXTERNAL_FILENAME.sh", parsed.AvailableScripts);
        }
        finally
        {
            if (linked)
            {
                Directory.Delete(scripts);
            }

            Directory.Delete(external, recursive: true);
        }
    }
}
