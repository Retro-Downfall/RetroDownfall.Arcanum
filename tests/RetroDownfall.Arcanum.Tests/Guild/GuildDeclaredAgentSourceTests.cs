using System.Text;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Guild;

[Collection(ProcessGlobalSeamCollectionName.Value)]
public sealed class GuildDeclaredAgentSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "guild-roster-" + Guid.NewGuid().ToString("N"));

    public GuildDeclaredAgentSourceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Direct_markdown_sources_are_fully_traversable_in_bounded_pages()
    {
        for (int i = 0; i < 107; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, $"agent-{i:D3}.md"), "---\nname: Agent\ndescription: Example\n---\nguidance");
        }

        Directory.CreateDirectory(Path.Combine(_root, "nested"));

        await File.WriteAllTextAsync(Path.Combine(_root, "nested", "hidden.md"), "---\nname: Hidden\ndescription: Example\n---");

        await File.WriteAllTextAsync(Path.Combine(_root, "ignored.txt"), "not an agent");

        GuildDeclaredAgentSourceReader reader = new();

        string? after = null;

        List<string> names = [];

        do
        {
            var result = await reader.ReadPageAsync(_root, _root, GuildAgentSourceKind.DeclaredGlobal, null, after, 17, CancellationToken.None);

            Assert.True(result.IsSuccess);

            Assert.InRange(result.Value.Items.Length, 1, 17);

            names.AddRange(result.Value.Items.Select(i => i.SourceIdentity));

            after = result.Value.NextName;
        } while (after is not null);

        Assert.Equal(107, names.Count);

        Assert.Equal(107, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Invalid_utf8_and_linked_leaf_are_diagnostics_without_payload()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "invalid.md"), [0xff, 0xfe]);

        string outside = Path.Combine(Path.GetTempPath(), "guild-outside-" + Guid.NewGuid().ToString("N"));

        try
        {
            await File.WriteAllTextAsync(outside, "---\nname: Outside\ndescription: Secret\n---\nsecret");

            File.CreateSymbolicLink(Path.Combine(_root, "linked.md"), outside);

            var result = await new GuildDeclaredAgentSourceReader().ReadPageAsync(_root, _root,
                GuildAgentSourceKind.DeclaredGlobal, null, null, 100, CancellationToken.None);

            Assert.True(result.IsSuccess);

            Assert.Equal(2, result.Value.Items.Length);

            Assert.All(result.Value.Items, item =>
            {
                Assert.Null(item.Agent);

                Assert.NotNull(item.ReasonCode);
            });
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Exact_byte_digest_changes_for_bom_and_unchanged_content_is_stable()
    {
        string file = Path.Combine(_root, "agent.md");

        byte[] bytes = Encoding.UTF8.GetBytes("---\nname: Agent\ndescription: Example\n---\nbody");

        await File.WriteAllBytesAsync(file, bytes);

        GuildDeclaredAgentSourceReader reader = new();

        var first = await reader.ReadPageAsync(_root, _root, GuildAgentSourceKind.DeclaredGlobal, null, null, 100, CancellationToken.None);

        var second = await reader.ReadPageAsync(_root, _root, GuildAgentSourceKind.DeclaredGlobal, null, null, 100, CancellationToken.None);

        Assert.Equal(first.Value.Items[0].Digest, second.Value.Items[0].Digest);

        await File.WriteAllBytesAsync(file, [0xef, 0xbb, 0xbf, .. bytes]);

        var bom = await reader.ReadPageAsync(_root, _root, GuildAgentSourceKind.DeclaredGlobal, null, null, 100, CancellationToken.None);

        Assert.NotEqual(first.Value.Items[0].Digest, bom.Value.Items[0].Digest);

        Assert.Equal(first.Value.Items[0].Agent!.Guidance, bom.Value.Items[0].Agent!.Guidance);
    }

    [Fact]
    public async Task Replacing_opened_leaf_cannot_publish_the_old_or_linked_payload()
    {
        string source = Path.Combine(_root, "agent.md");

        string moved = Path.Combine(_root, "old.txt");

        await File.WriteAllTextAsync(source, "---\nname: Agent\ndescription: Original\n---\nsecret");

        SecureFileReader.AfterOpenForTests = _ =>
        {
            File.Move(source, moved);

            File.CreateSymbolicLink(source, moved);
        };

        try
        {
            var result = await new GuildDeclaredAgentSourceReader().ReadPageAsync(_root, _root,
                GuildAgentSourceKind.DeclaredGlobal, null, null, 100, CancellationToken.None);

            Assert.True(result.IsSuccess);

            Assert.Null(Assert.Single(result.Value.Items).Agent);
        }
        finally
        {
            SecureFileReader.AfterOpenForTests = null;
        }
    }

    [Fact]
    public async Task Linked_agent_directory_is_refused_instead_of_scanned()
    {
        string outside = Path.Combine(Path.GetTempPath(), "guild-outside-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(outside);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside, "secret.md"), "---\nname: Secret\ndescription: Outside\n---");

            Directory.CreateSymbolicLink(Path.Combine(_root, "agents"), outside);

            var result = await new GuildDeclaredAgentSourceReader().ReadPageAsync(_root, Path.Combine(_root, "agents"),
                GuildAgentSourceKind.DeclaredWorkspace, Guid.NewGuid(), null, 100, CancellationToken.None);

            Assert.False(result.IsSuccess);

            Assert.Equal("Guild.WorkspaceIdentityUnavailable", result.Error.Code);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Oversize_authored_file_is_diagnosed_without_truncating_it()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "large.md"), "---\nname: Agent\ndescription: Large\n---\n"
            + new string('x', checked((int)ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes())));

        var result = await new GuildDeclaredAgentSourceReader().ReadPageAsync(_root, _root,
            GuildAgentSourceKind.DeclaredGlobal, null, null, 100, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("Guild.PayloadTooLarge", Assert.Single(result.Value.Items).ReasonCode);

        Assert.Null(result.Value.Items[0].Agent);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
