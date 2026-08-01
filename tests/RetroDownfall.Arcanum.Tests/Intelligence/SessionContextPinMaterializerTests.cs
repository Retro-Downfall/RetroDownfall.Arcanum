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
    public async Task Oversized_file_pin_fails_closed_without_materializing_content()
    {

        string file = Path.Combine(_workspace, "oversized.bin");

        await using (FileStream stream = new(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {

            stream.SetLength(SessionContextPinMaterializer.MaxSourceFileBytes + 1);

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

        Assert.Contains("safe materialization limit", text, StringComparison.Ordinal);

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
    public async Task Oversized_symbol_source_fails_closed_before_scanning()
    {

        string file = Path.Combine(_workspace, "oversized-lines.txt");

        await using (FileStream stream = new(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {

            stream.SetLength(SessionContextPinMaterializer.MaxSourceFileBytes + 1);

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

        Assert.Contains("safe materialization limit", text, StringComparison.Ordinal);

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

    private SessionContextPinMaterializer Create(SessionContextPinRecord pin) =>
        new(new StaticPinStore(pin), new NoOpSessionAttachmentStore(), _db!);

    private static SessionContextPinRecord Pin(
        SessionContextPinKind kind, string target, string label, string? version) =>
        new(Guid.NewGuid(), Guid.NewGuid(), kind, target, label, version, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class StaticPinStore(SessionContextPinRecord pin) : ISessionContextPinStore
    {
        public Task<IReadOnlyList<SessionContextPinRecord>> ListAsync(
            Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionContextPinRecord>>([pin]);

        public Task<SessionContextPinRecord> UpsertAsync(
            Guid sessionId, SessionContextPinKind kind, string targetIdentifier, string displayLabel,
            string? contentVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            Guid sessionId, Guid pinId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
