using Microsoft.Extensions.AI;

using System.Security.Cryptography;

using System.Text;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

[Collection("Grimoire")]
public sealed class SessionContextPinMaterializerTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private string _dbPath = string.Empty;

    private string _workspace = string.Empty;

    private ArcanumDbContext? _db;

    public Task InitializeAsync()
    {
        _dbPath = fixture.CopyDatabase();
        _db = fixture.CreateContext(_dbPath);
        _workspace = Path.Combine(Path.GetTempPath(), "arcanum-pin-materializer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
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

        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Fact]
    public async Task File_pin_is_labeled_untrusted_and_modified_hash_is_reported()
    {
        string file = Path.Combine(_workspace, "notes.txt");
        await File.WriteAllTextAsync(file, "current bytes");
        SessionContextPinRecord pin = Pin(
            SessionContextPinKind.File, "notes.txt", "notes", new string('0', 64));
        SessionContextPinMaterialization result = await Create(pin).MaterializeAsync(
            pin.SessionId, _workspace, CancellationToken.None);
        string text = Assert.IsType<TextContent>(Assert.Single(result.Contents)).Text;

        Assert.Contains("UNTRUSTED SESSION CONTEXT DATA", text, StringComparison.Ordinal);
        Assert.Contains("status: Modified", text, StringComparison.Ordinal);
        Assert.Contains("current bytes", text, StringComparison.Ordinal);
        Assert.Contains("sha256=", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lexical_workspace_escape_fails_closed_without_reading_content()
    {
        string outside = Path.Combine(Path.GetDirectoryName(_workspace)!, "outside-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(outside, "secret");
        try
        {
            SessionContextPinRecord pin = Pin(SessionContextPinKind.File, "../" + Path.GetFileName(outside), "escape", null);
            SessionContextPinMaterialization result = await Create(pin).MaterializeAsync(
                pin.SessionId, _workspace, CancellationToken.None);
            string text = Assert.IsType<TextContent>(Assert.Single(result.Contents)).Text;
            Assert.Contains("status: Unsafe", text, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Bounded_file_reader_hashes_full_stream_without_materializing_full_content()
    {

        byte[] payload = Encoding.UTF8.GetBytes(new string('x', 2 * 1024 * 1024));

        using MemoryStream stream = new(payload, writable: false);

        SessionContextPinMaterializer.BoundedFileRead read =
            await SessionContextPinMaterializer.ReadBoundedFileAsync(
                stream,
                4096,
                CancellationToken.None);

        Assert.True(read.Truncated);

        Assert.Equal(4096, Encoding.UTF8.GetByteCount(read.Content));

        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            read.Sha256);

    }

    [Fact]
    public async Task Large_file_pin_streams_a_bounded_preview_and_full_hash()
    {

        string file = Path.Combine(_workspace, "oversized.bin");

        await using (FileStream stream = new(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {

            stream.SetLength(64L * 1024L * 1024L + 1L);

        }

        SessionContextPinRecord pin = Pin(
            SessionContextPinKind.File,
            "oversized.bin",
            "oversized",
            null);

        SessionContextPinMaterialization result = await Create(pin).MaterializeAsync(
            pin.SessionId,
            _workspace,
            CancellationToken.None);

        string text = Assert.IsType<TextContent>(Assert.Single(result.Contents)).Text;

        Assert.Contains("status: Truncated", text, StringComparison.Ordinal);

        Assert.Contains("sha256=", text, StringComparison.Ordinal);

        Assert.DoesNotContain("safe materialization limit", text, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Symbol_range_normalizes_crlf_line_endings()
    {

        string file = Path.Combine(_workspace, "lines.txt");

        await File.WriteAllTextAsync(file, "first\r\nsecond\r\nthird\r\nfourth\r\n");

        SessionContextPinRecord pin = Pin(
            SessionContextPinKind.SymbolRange,
            "lines.txt:2-3",
            "lines",
            null);

        SessionContextPinMaterialization result = await Create(pin).MaterializeAsync(
            pin.SessionId,
            _workspace,
            CancellationToken.None);

        string text = Assert.IsType<TextContent>(Assert.Single(result.Contents)).Text;

        Assert.Contains("second\nthird", text, StringComparison.Ordinal);

        Assert.DoesNotContain('\r', text);

    }

    [Fact]
    public async Task Large_symbol_source_streams_only_the_requested_range()
    {

        string file = Path.Combine(_workspace, "oversized-lines.txt");

        await using (FileStream stream = new(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {

            byte[] firstLine = "requested line\n"u8.ToArray();

            await stream.WriteAsync(firstLine);

            stream.SetLength(64L * 1024L * 1024L + 1L);

        }

        SessionContextPinRecord pin = Pin(
            SessionContextPinKind.SymbolRange,
            "oversized-lines.txt:1-1",
            "oversized-lines",
            null);

        SessionContextPinMaterialization result = await Create(pin).MaterializeAsync(
            pin.SessionId,
            _workspace,
            CancellationToken.None);

        string text = Assert.IsType<TextContent>(Assert.Single(result.Contents)).Text;

        Assert.Contains("requested line", text, StringComparison.Ordinal);

        Assert.DoesNotContain("safe materialization limit", text, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Symbol_range_has_no_arbitrary_line_count_ceiling()
    {

        string file = Path.Combine(_workspace, "many-lines.txt");

        string contents = string.Join(
            '\n',
            Enumerable.Range(1, 2_101).Select(static line => $"line-{line}"));

        await File.WriteAllTextAsync(file, contents);

        SessionContextPinRecord pin = Pin(
            SessionContextPinKind.SymbolRange,
            "many-lines.txt:1-2101",
            "many-lines",
            null);

        SessionContextPinMaterialization result = await Create(pin).MaterializeAsync(
            pin.SessionId,
            _workspace,
            CancellationToken.None);

        string text = Assert.IsType<TextContent>(Assert.Single(result.Contents)).Text;

        Assert.Contains("line-2101", text, StringComparison.Ordinal);

        Assert.DoesNotContain("Invalid or excessive line range", text, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task Directory_snapshot_never_enumerates_through_a_symlink_outside_the_workspace()
    {

        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Symlink-escape containment is exercised on Unix hosts.");

        string outside = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pin-outside-{Guid.NewGuid():N}");

        Directory.CreateDirectory(outside);

        string marker = $"outside-secret-{Guid.NewGuid():N}.txt";

        await File.WriteAllTextAsync(
            Path.Combine(outside, marker),
            "secret");

        string link = Path.Combine(_workspace, "escape-directory");

        Directory.CreateSymbolicLink(link, outside);

        try
        {

            await File.WriteAllTextAsync(
                Path.Combine(_workspace, "visible.txt"),
                "visible");

            SessionContextPinRecord pin = Pin(
                SessionContextPinKind.DirectorySnapshot,
                ".",
                "workspace",
                null);

            SessionContextPinMaterialization result =
                await Create(pin).MaterializeAsync(
                    pin.SessionId,
                    _workspace,
                    CancellationToken.None);

            string text = Assert.IsType<TextContent>(
                Assert.Single(result.Contents)).Text;

            Assert.Contains("visible.txt", text, StringComparison.Ordinal);

            Assert.DoesNotContain(marker, text, StringComparison.Ordinal);

        }
        finally
        {

            Directory.Delete(link);

            Directory.Delete(outside, recursive: true);

        }

    }

    [SkippableFact]
    public async Task Directory_snapshot_visits_a_canonical_directory_only_once_across_symlink_cycles()
    {

        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Symlink-cycle containment is exercised on Unix hosts.");

        await File.WriteAllTextAsync(
            Path.Combine(_workspace, "cycle-visible.txt"),
            "visible");

        string nested = Path.Combine(_workspace, "nested");

        Directory.CreateDirectory(nested);

        string link = Path.Combine(nested, "back-to-root");

        Directory.CreateSymbolicLink(link, _workspace);

        try
        {

            SessionContextPinRecord pin = Pin(
                SessionContextPinKind.DirectorySnapshot,
                ".",
                "workspace",
                null);

            SessionContextPinMaterialization result =
                await Create(pin).MaterializeAsync(
                    pin.SessionId,
                    _workspace,
                    CancellationToken.None);

            string text = Assert.IsType<TextContent>(
                Assert.Single(result.Contents)).Text;

            Assert.Equal(
                1,
                CountOccurrences(text, "cycle-visible.txt"));

            Assert.DoesNotContain(
                "nested/back-to-root/",
                text,
                StringComparison.Ordinal);

        }
        finally
        {

            Directory.Delete(link);

        }

    }

    [SkippableFact]
    public async Task Directory_snapshot_preserves_access_through_a_contained_noncyclic_directory_symlink()
    {

        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Symlink containment is exercised on Unix hosts.");

        string shared = Path.Combine(_workspace, "shared");

        Directory.CreateDirectory(shared);

        await File.WriteAllTextAsync(
            Path.Combine(shared, "allowed.txt"),
            "allowed");

        string scope = Path.Combine(_workspace, "scope");

        Directory.CreateDirectory(scope);

        string link = Path.Combine(scope, "linked-shared");

        Directory.CreateSymbolicLink(link, shared);

        try
        {

            SessionContextPinRecord pin = Pin(
                SessionContextPinKind.DirectorySnapshot,
                "scope",
                "scope",
                null);

            SessionContextPinMaterialization result =
                await Create(pin).MaterializeAsync(
                    pin.SessionId,
                    _workspace,
                    CancellationToken.None);

            string text = Assert.IsType<TextContent>(
                Assert.Single(result.Contents)).Text;

            Assert.Contains(
                Path.Combine(
                    "linked-shared",
                    "allowed.txt"),
                text,
                StringComparison.Ordinal);

        }
        finally
        {

            Directory.Delete(link);

        }

    }

    [Fact]
    public async Task Per_turn_truncation_suffix_is_included_inside_the_exact_byte_budget()
    {

        Guid sessionId = Guid.NewGuid();

        string payload = new(
            'x',
            SessionContextPinMaterializer.MaxBytesPerPin * 2);

        List<SessionContextPinRecord> pins = [];

        for (int index = 0; index < 4; index++)
        {

            string relativePath = $"budget-{index}.txt";

            await File.WriteAllTextAsync(
                Path.Combine(_workspace, relativePath),
                payload);

            pins.Add(
                new SessionContextPinRecord(
                    Guid.NewGuid(),
                    sessionId,
                    SessionContextPinKind.File,
                    relativePath,
                    $"file-{index}",
                    ContentVersion: null,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow));

        }

        SessionContextPinMaterialization result =
            await Create([.. pins]).MaterializeAsync(
                sessionId,
                _workspace,
                CancellationToken.None);

        int actualBytes = result.Contents
            .OfType<TextContent>()
            .Sum(static content => Encoding.UTF8.GetByteCount(content.Text));

        Assert.Equal(
            SessionContextPinMaterializer.MaxBytesPerTurn,
            result.IncludedBytes);

        Assert.Equal(pins.Count, result.Contents.Count);

        Assert.Equal(0, result.OmittedCount);

        Assert.Equal(result.IncludedBytes, actualBytes);

        string finalBlock = Assert.IsType<TextContent>(
            result.Contents[^1]).Text;

        Assert.EndsWith(
            "[TRUNCATED BY PER-TURN CONTEXT BUDGET]",
            finalBlock,
            StringComparison.Ordinal);

    }

    [Fact]

    public async Task Image_attachment_pin_is_retained_but_reports_unsupported_implicit_materialization()

    {

        Guid sessionId = Guid.NewGuid();

        Guid attachmentId = Guid.NewGuid();

        SessionContextPinRecord pin = new(
            Guid.NewGuid(),
            sessionId,
            SessionContextPinKind.Attachment,
            attachmentId.ToString("D"),
            "map.png",
            "image-version",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        SessionAttachmentRecord attachment = new(
            attachmentId,
            sessionId,
            EntryId: null,
            PendingTurnId: null,
            SessionAttachmentState.Bound,
            LogicalKey: "map",
            OriginalFileName: "map.png",
            Version: 1,
            RelativePath: "session/map/v1/map.png",
            ContentSha256: new string('a', 64),
            MimeType: "image/png",
            ByteLength: 8,
            SessionAttachmentKind.Image,
            DateTimeOffset.UtcNow);

        SessionContextPinMaterializer materializer = new(
            new StaticPinStore(pin),
            new NoOpSessionAttachmentStore(attachment),
            _db!);

        SessionContextPinMaterialization result = await materializer.MaterializeAsync(
            sessionId,
            _workspace,
            CancellationToken.None);

        string text = Assert.IsType<TextContent>(Assert.Single(result.Contents)).Text;

        Assert.Contains("status: Unsupported", text, StringComparison.Ordinal);

        Assert.Contains("explicit attachment reference", text, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("status: Missing", text, StringComparison.Ordinal);

    }

    private SessionContextPinMaterializer Create(
        params SessionContextPinRecord[] pins) =>
        new(new StaticPinStore(pins), new NoOpSessionAttachmentStore(), _db!);

    private static SessionContextPinRecord Pin(
        SessionContextPinKind kind, string target, string label, string? version) =>
        new(Guid.NewGuid(), Guid.NewGuid(), kind, target, label, version, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static int CountOccurrences(
        string value,
        string search)
    {

        int count = 0;

        int offset = 0;

        while ((offset = value.IndexOf(
                   search,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {

            count++;

            offset += search.Length;

        }

        return count;

    }

    private sealed class StaticPinStore(
        params SessionContextPinRecord[] pins) : ISessionContextPinStore
    {
        public Task<IReadOnlyList<SessionContextPinRecord>> ListAsync(
            Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionContextPinRecord>>(pins);

        public Task<SessionContextPinRecord> UpsertAsync(
            Guid sessionId, SessionContextPinKind kind, string targetIdentifier, string displayLabel,
            string? contentVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            Guid sessionId, Guid pinId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
