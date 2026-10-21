using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

namespace RetroDownfall.Arcanum.Tests.Intelligence.Spells;

public sealed class SpellDirectoryTests
{
    [Fact]
    public async Task Atomic_write_rejects_a_substituted_source_at_the_actual_rename_boundary()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        string destination = Path.Combine(root, "SPELL.md");

        await File.WriteAllTextAsync(destination, "ORIGINAL SPELL");

        bool substituted = false;

        SpellDirectory.BeforeRenameForTests = source =>
        {
            if (!substituted)
            {
                File.Move(source, source + ".original");

                File.WriteAllText(source, "SUBSTITUTED CONTENT");

                substituted = true;
            }
        };

        try
        {
            using SpellDirectory directory = SpellDirectory.Open(root, root);

            await Assert.ThrowsAsync<IOException>(() => directory.WriteTextAsync("SPELL.md", "INTENDED CONTENT", CancellationToken.None));

            Assert.True(substituted);

            Assert.Equal("ORIGINAL SPELL", await File.ReadAllTextAsync(destination));
        }
        finally
        {
            SpellDirectory.BeforeRenameForTests = null;

            Directory.Delete(root, recursive: true);
        }
    }
}
