using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Tower;

namespace RetroDownfall.Arcanum.Tests.Guild;

[Collection("WorkspacePathPolicy")]
public sealed class GuildWorkspaceReadToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("guild-workspace-read-").FullName;

    private readonly PhysicalCampaignRootOpener _opener = new(new KeySource());

    private readonly byte[] _cursorKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    private readonly Guid _workspaceId = Guid.NewGuid();

    private Func<CancellationToken, Task<Result>> _reauthorize = _ => Task.FromResult(Result.Success());

    [Fact]
    public async Task Read_pages_preserve_unicode_and_line_terminators_and_bind_the_cursor()
    {
        string input = string.Concat(Enumerable.Repeat("a🌻β", 250)) + "\r\n" + " two \rthree\nlast";

        WriteFile("allowed/read.txt", input);

        WriteFile("allowed/other.txt", "other");

        var (physical, grant) = Authority("allowed");

        const int pageBytes = 768;

        string? cursor = null;

        StringBuilder reconstructed = new();

        do
        {
            string arguments = ReadArguments("allowed/read.txt", cursor);

            Result<string> result = await ExecuteAsync("read_file", arguments, physical, grant, pageBytes);

            Assert.True(result.IsSuccess, result.Error.ToString());

            Assert.InRange(Encoding.UTF8.GetByteCount(result.Value), 1, pageBytes);

            using JsonDocument page = JsonDocument.Parse(result.Value);

            reconstructed.Append(page.RootElement.GetProperty("content").GetString());

            cursor = page.RootElement.GetProperty("continuation").GetString();

            if (cursor is not null)
            {
                Assert.True((await ExecuteAsync("read_file", ReadArguments("allowed/other.txt", cursor), physical, grant, pageBytes)).IsFailure);

                Assert.True((await ExecuteAsync("read_file", ReadArguments("allowed/read.txt", (cursor[0] == 'A' ? "B" : "A") + cursor[1..]), physical, grant, pageBytes)).IsFailure);
            }
        }
        while (cursor is not null);

        Assert.Equal(input, reconstructed.ToString());
    }

    [Fact]
    public async Task List_and_search_disclose_only_authorized_paths_before_the_page_limit()
    {
        for (int i = 0; i < 260; i++)
        {
            WriteFile($"private/{i:D3}.txt", "needle");
        }

        WriteFile("shared/one.txt", "needle");

        WriteFile("shared/deeper/two.txt", "needle");

        var (physical, grant) = Authority("shared");

        Result<string> listing = await ExecuteAsync("list_directory", """{"relativePath":".","recursive":true}""", physical, grant);

        Assert.True(listing.IsSuccess, listing.Error.ToString());

        using JsonDocument list = JsonDocument.Parse(listing.Value);

        Assert.Equal(["shared", "shared/deeper", "shared/deeper/two.txt", "shared/one.txt"],
            list.RootElement.GetProperty("paths").EnumerateArray().Select(path => path.GetString()));

        Result<string> search = await ExecuteAsync("search_workspace",
            """{"pattern":"needle","mode":"literal","caseSensitive":true,"globs":["**/*.txt"]}""", physical, grant);

        Assert.True(search.IsSuccess, search.Error.ToString());

        using JsonDocument found = JsonDocument.Parse(search.Value);

        Assert.Equal(["shared/deeper/two.txt", "shared/one.txt"],
            found.RootElement.GetProperty("matches").EnumerateArray().Select(match => match.GetProperty("path").GetString()));
    }

    [Fact]
    public async Task A_read_cursor_will_not_resume_into_a_replacement_leaf()
    {
        WriteFile("allowed/read.txt", new('x', 1800));

        var (physical, grant) = Authority("allowed");

        Result<string> first = await ExecuteAsync("read_file", ReadArguments("allowed/read.txt", null), physical, grant, 768);

        Assert.True(first.IsSuccess, first.Error.ToString());

        using JsonDocument page = JsonDocument.Parse(first.Value);

        string? cursor = page.RootElement.GetProperty("continuation").GetString();

        Assert.NotNull(cursor);

        string path = Path.Combine(_root, "allowed/read.txt");

        File.Move(path, path + ".before");

        WriteFile("allowed/read.txt", new('x', 1800));

        Assert.True((await ExecuteAsync("read_file", ReadArguments("allowed/read.txt", cursor), physical, grant, 768)).IsFailure);
    }

    [Fact]
    public async Task A_renamed_leaf_after_read_cannot_be_disclosed()
    {
        WriteFile("allowed/read.txt", "original");

        var (physical, grant) = Authority("allowed");

        string path = Path.Combine(_root, "allowed/read.txt");

        int checks = 0;

        _reauthorize = _ =>
        {
            if (++checks == 2)
            {
                File.Move(path, path + ".before");

                WriteFile("allowed/read.txt", "replacement");
            }

            return Task.FromResult(Result.Success());
        };

        Assert.True((await ExecuteAsync("read_file", ReadArguments("allowed/read.txt", null), physical, grant)).IsFailure);
    }

    [Fact]
    public async Task A_complete_short_read_does_not_require_room_for_a_cursor()
    {
        WriteFile("allowed/á🌻.txt", "x\r\n");

        var (physical, grant) = Authority("allowed");

        const int pageBytes = 40;

        Result<string> result = await ExecuteAsync("read_file", ReadArguments("allowed/á🌻.txt", null), physical, grant, pageBytes);

        Assert.True(result.IsSuccess, result.Error.ToString());

        Assert.InRange(Encoding.UTF8.GetByteCount(result.Value), 1, pageBytes);

        using JsonDocument page = JsonDocument.Parse(result.Value);

        Assert.Equal("x", page.RootElement.GetProperty("content").GetString());

        Assert.Null(page.RootElement.GetProperty("continuation").GetString());
    }

    [Fact]
    public async Task A_page_cannot_disclose_rewritten_bytes_when_leaf_and_mtime_are_unchanged()
    {
        WriteFile("allowed/read.txt", "original");

        var (physical, grant) = Authority("allowed");

        string path = Path.Combine(_root, "allowed/read.txt");

        DateTime mtime = File.GetLastWriteTimeUtc(path);

        int checks = 0;

        _reauthorize = _ =>
        {
            if (++checks == 2)
            {
                File.WriteAllText(path, "replaced");

                File.SetLastWriteTimeUtc(path, mtime);
            }

            return Task.FromResult(Result.Success());
        };

        Assert.True((await ExecuteAsync("read_file", ReadArguments("allowed/read.txt", null), physical, grant)).IsFailure);
    }

    [Theory]
    [InlineData("list_directory", "continuation")]
    [InlineData("search_workspace", "nextCursor")]
    public async Task Directory_and_search_pages_advance_without_duplicates_and_bind_scope(string tool, string cursorName)
    {
        string[] expected = [.. Enumerable.Range(0, 10).Select(index => $"allowed/{index:D2}-{new string('x', 120)}.txt")];

        foreach (string path in expected)
        {
            WriteFile(path, "needle");
        }

        var (physical, grant) = Authority("allowed");

        const int pageBytes = 712;

        string? cursor = null;

        List<string?> observed = [];

        int pages = 0;

        do
        {
            string args = PagedArguments(tool, cursor);

            Result<string> result = await ExecuteAsync(tool, args, physical, grant, pageBytes);

            Assert.True(result.IsSuccess, result.Error.ToString());

            Assert.InRange(Encoding.UTF8.GetByteCount(result.Value), 1, pageBytes);

            using JsonDocument page = JsonDocument.Parse(result.Value);

            if (tool == "list_directory")
            {
                observed.AddRange(page.RootElement.GetProperty("paths").EnumerateArray().Select(path => path.GetString()));
            }
            else
            {
                Assert.False(page.RootElement.TryGetProperty("_matchCursors", out _));

                observed.AddRange(page.RootElement.GetProperty("matches").EnumerateArray().Select(match => match.GetProperty("path").GetString()));
            }

            cursor = page.RootElement.TryGetProperty(cursorName, out JsonElement continuation) ? continuation.GetString() : null;

            if (++pages == 1)
            {
                Assert.NotNull(cursor);

                Assert.True((await ExecuteAsync(tool, PagedArguments(tool, cursor), physical, grant, pageBytes, new byte[32])).IsFailure);

                Assert.True((await ExecuteAsync(tool, PagedArguments(tool, cursor, "."), physical, grant, pageBytes)).IsFailure);
            }

            Assert.InRange(pages, 1, expected.Length);
        }
        while (cursor is not null);

        Assert.Equal(expected, observed);
    }

    private static string PagedArguments(string tool, string? cursor, string path = "allowed")
    {
        System.Text.Json.Nodes.JsonObject args = tool == "list_directory"
            ? new() { ["relativePath"] = path, ["recursive"] = true, ["continuation"] = cursor }
            : new() { ["root"] = path, ["pattern"] = "needle", ["mode"] = "literal", ["caseSensitive"] = true, ["cursor"] = cursor };

        return args.ToJsonString();
    }

    private async Task<Result<string>> ExecuteAsync(string toolName, string arguments, GuildWorkspaceAuthority authority,
        GuildGrant grant, long pageBytes = 4_096, byte[]? key = null)
    {
        using JsonDocument json = JsonDocument.Parse(arguments);

        return await GuildWorkspaceReadTools.ExecuteAsync(toolName, json.RootElement, authority, grant,
            _workspaceId, pageBytes, new WorkspaceSearchSettings(), key ?? _cursorKey, _reauthorize, CancellationToken.None);
    }

    private static string ReadArguments(string path, string? cursor) =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["relativePath"] = path,
            ["startLine"] = 1,
            ["endLine"] = 4,
            ["continuation"] = cursor,
        }.ToJsonString();

    private (GuildWorkspaceAuthority Authority, GuildGrant Grant) Authority(string relativeRoot)
    {
        GuildWorkspaceRegistration registration = new(_workspaceId, _root, 1, 1, _opener.IdentifyExact(_root)!.Value);

        GuildWorkspaceAuthority physical = new(registration, _opener,
            _ => Task.FromResult(Result<GuildWorkspaceRegistration>.Success(registration)));

        GuildGrant grant = new([new(_workspaceId, relativeRoot, GuildWorkspaceAccess.Read)], [], [],
            new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null);

        return (physical, grant);
    }

    private void WriteFile(string path, string content)
    {
        string absolute = Path.Combine(_root, path);

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        File.WriteAllText(absolute, content);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class KeySource : ICampaignRootIdentityKeyProvider
    {
        public bool TryCopyRootIdentityKey(Span<byte> destination)
        {
            destination[..32].Fill(0x42);

            return true;
        }
    }
}
