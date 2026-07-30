using Microsoft.Extensions.AI;
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
