using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class SecureStorageNoticeTests
{
    [Fact]
    public void Immediate_notice_explains_both_possible_secure_storage_keys()
    {
        RecordingConsole console = new();

        SecureStorageNotice notice = new(
            console,
            bootstrapMarkerExists: static () => false,
            markBootstrapCompleted: static () => { },
            secureStoreName: "macOS Keychain");

        notice.ExplainBeforeHostBootstrap();

        string output = string.Join('\n', console.Diagnostics);

        Assert.Contains("server-authentication key", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read or create", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("macOS Keychain", output, StringComparison.Ordinal);
        Assert.Contains("login password", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never receives", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("separate file-encryption key", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("existing encrypted files", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("may also read", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("otherwise", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attachments, uploads, or batch files", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("is not being created now", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Setup_notice_explains_what_the_first_server_start_will_do()
    {
        RecordingConsole console = new();

        SecureStorageNotice notice = new(
            console,
            bootstrapMarkerExists: static () => false,
            markBootstrapCompleted: static () => { },
            secureStoreName: "macOS Keychain");

        notice.ExplainAfterSetup();

        string output = string.Join('\n', console.Diagnostics);

        Assert.Contains("after setup", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("first server start", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read or create", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("macOS Keychain", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Completed_bootstrap_marker_suppresses_first_run_notices()
    {
        RecordingConsole console = new();

        SecureStorageNotice notice = new(
            console,
            bootstrapMarkerExists: static () => true,
            markBootstrapCompleted: static () => { },
            secureStoreName: "macOS Keychain");

        notice.ExplainBeforeHostBootstrap();
        notice.ExplainAfterSetup();

        Assert.Empty(console.Diagnostics);
    }

    [Fact]
    public void Successful_host_bootstrap_publishes_the_lifecycle_marker_once()
    {
        RecordingConsole console = new();
        bool completed = false;
        int publishCount = 0;

        SecureStorageNotice notice = new(
            console,
            bootstrapMarkerExists: () => completed,
            markBootstrapCompleted: () =>
            {
                completed = true;
                publishCount++;
            },
            secureStoreName: "macOS Keychain");

        notice.ExplainBeforeHostBootstrap();
        notice.MarkHostBootstrapCompleted();
        notice.ExplainBeforeHostBootstrap();

        Assert.Equal(1, publishCount);
        Assert.Equal(2, console.Diagnostics.Count);
    }

    [Fact]
    public void Marker_write_failure_does_not_stop_a_running_host()
    {
        RecordingConsole console = new();

        SecureStorageNotice notice = new(
            console,
            bootstrapMarkerExists: static () => false,
            markBootstrapCompleted: static () => throw new IOException("disk unavailable"),
            secureStoreName: "macOS Keychain");

        Exception? exception = Record.Exception(notice.MarkHostBootstrapCompleted);

        Assert.Null(exception);
        Assert.Contains(
            console.Diagnostics,
            line => line.Contains("may see", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecordingConsole : IConsoleDispatcher
    {
        public List<string> Diagnostics { get; } = [];

        public void WritePayload(string value)
        {
        }

        public void WriteDiagnostic(string value) => Diagnostics.Add(value);

        public void WriteVerbose(string value)
        {
        }

        public void WriteJson<T>(T value, JsonTypeInfo<T> typeInfo)
        {
        }

        public void WriteJson(JsonElement value)
        {
        }

        public void BeginJsonStream()
        {
        }
    }
}
