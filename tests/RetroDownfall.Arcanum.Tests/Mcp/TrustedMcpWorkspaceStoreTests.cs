using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RetroDownfall.Arcanum.Tests.Mcp;

[Collection("ProcessEnvironment")]
public sealed class TrustedMcpWorkspaceStoreTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    private string _storePath = string.Empty;

    private string? _originalDotnetEnvironment;

    private string? _originalAspNetCoreEnvironment;

    private string? _originalTestHome;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _originalDotnetEnvironment = global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        _originalAspNetCoreEnvironment = global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        _originalTestHome = global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _workspace.Root);

        _storePath = Path.Combine(ArcanumPaths.GrimoireDirectory, "trusted-mcp-workspaces.json");

        Assert.StartsWith(
            Path.GetFullPath(_workspace.Root),
            Path.GetFullPath(_storePath),
            StringComparison.Ordinal);
    }

    public async Task DisposeAsync()
    {

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests = null;

        if (File.Exists(_storePath))
        {

            File.Delete(_storePath);

        }

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", _originalDotnetEnvironment);

        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", _originalAspNetCoreEnvironment);

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _originalTestHome);

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task IsTrustedAsync_returns_false_when_mcp_json_missing()
    {

        using TrustedMcpWorkspaceStore store = new();

        bool trusted = await store.IsTrustedAsync(_workspace.Root);

        Assert.False(trusted);

    }

    [SkippableFact]
    public async Task TrustAsync_rejects_symlinked_mcp_json()
    {

        Skip.IfNot(
            OperatingSystem.IsMacOS()
            || OperatingSystem.IsLinux(),
            "Symlink semantics are Unix-only.");

        string outside = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-trust-outside-{Guid.NewGuid():N}.json");

        await File.WriteAllTextAsync(
            outside,
            """{"mcpServers":{}}""");

        string link = Path.Combine(_workspace.Root, "mcp.json");

        File.CreateSymbolicLink(link, outside);

        try
        {
            using TrustedMcpWorkspaceStore store = new();

            TrustedMcpWorkspaceStoreException error =
                await Assert.ThrowsAsync<TrustedMcpWorkspaceStoreException>(
                    () => store.TrustAsync(_workspace.Root));

            Assert.DoesNotContain(
                outside,
                error.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(link);

            File.Delete(outside);
        }

    }

    [Fact]
    public async Task TrustAsync_rejects_hard_linked_mcp_json()
    {

        string mcpPath = _workspace.WriteFile(
            "mcp.json",
            """{"mcpServers":{}}""");

        string outsideAlias = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-trust-hardlink-{Guid.NewGuid():N}.json");

        try
        {
            Assert.True(
                HardLinkTestSupport.TryCreate(
                    outsideAlias,
                    mcpPath));

            using TrustedMcpWorkspaceStore store = new();

            TrustedMcpWorkspaceStoreException error =
                await Assert.ThrowsAsync<TrustedMcpWorkspaceStoreException>(
                    () => store.TrustAsync(_workspace.Root));

            Assert.DoesNotContain(
                outsideAlias,
                error.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outsideAlias);
        }

    }

    [Fact]
    public async Task TrustAsync_then_IsTrustedAsync_returns_true_for_matching_hash()
    {

        string mcpPath = _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        using TrustedMcpWorkspaceStore store = new();

        await store.TrustAsync(_workspace.Root);

        bool trusted = await store.IsTrustedAsync(_workspace.Root);

        Assert.True(trusted);

        Assert.True(File.Exists(_storePath));

    }

    [Fact]
    public async Task TrustAsync_without_mcp_json_throws_FileNotFoundException()
    {

        using TrustedMcpWorkspaceStore store = new();

        await Assert.ThrowsAsync<FileNotFoundException>(() => store.TrustAsync(_workspace.Root));

    }

    [Fact]
    public async Task IsTrustedAsync_returns_false_after_mcp_json_changes()
    {

        _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        using TrustedMcpWorkspaceStore store = new();

        await store.TrustAsync(_workspace.Root);

        _workspace.WriteFile("mcp.json", """{"mcpServers":{"x":{}}}""");

        bool trusted = await store.IsTrustedAsync(_workspace.Root);

        Assert.False(trusted);

    }

    [Fact]
    public async Task IsTrustedAsync_detects_same_size_byte_change_even_when_mtime_restored()
    {

        // Same length so a naive mtime+length cache would still treat the file as unchanged.
        const string original = """{"mcpServers":{"a":{}}}""";
        const string modified = """{"mcpServers":{"b":{}}}""";

        Assert.Equal(Encoding.UTF8.GetByteCount(original), Encoding.UTF8.GetByteCount(modified));

        string mcpPath = _workspace.WriteFile("mcp.json", original);

        DateTime originalWriteUtc = File.GetLastWriteTimeUtc(mcpPath);

        using TrustedMcpWorkspaceStore store = new();

        await store.TrustAsync(_workspace.Root);

        Assert.True(await store.IsTrustedAsync(_workspace.Root));

        _workspace.WriteFile("mcp.json", modified);

        File.SetLastWriteTimeUtc(mcpPath, originalWriteUtc);

        Assert.Equal(originalWriteUtc, File.GetLastWriteTimeUtc(mcpPath));

        Assert.Equal(Encoding.UTF8.GetByteCount(original), new FileInfo(mcpPath).Length);

        bool trusted = await store.IsTrustedAsync(_workspace.Root);

        Assert.False(trusted);

    }

    [Fact]
    public async Task IsTrustedAsync_rereads_bytes_when_file_unchanged()
    {

        string mcpPath = _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        using TrustedMcpWorkspaceStore store = new();

        await store.TrustAsync(_workspace.Root);

        string firstHash = await ComputeSha256HexAsync(mcpPath);

        bool firstCheck = await store.IsTrustedAsync(_workspace.Root);

        bool secondCheck = await store.IsTrustedAsync(_workspace.Root);

        Assert.True(firstCheck);

        Assert.True(secondCheck);

        Assert.Equal(firstHash, await ComputeSha256HexAsync(mcpPath));

    }

    [Fact]
    public async Task IsApprovedDigestAsync_compares_the_exact_digest_approved_by_TrustAsync()
    {

        const string exactBytes = "{\n  \"mcpServers\": {}\n}\n";

        string mcpPath = _workspace.WriteFile("mcp.json", exactBytes);

        string approvedDigest = await ComputeSha256HexAsync(mcpPath);

        string differentDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exactBytes.Trim())));

        using TrustedMcpWorkspaceStore store = new();

        await store.TrustAsync(_workspace.Root);

        Assert.True(await store.IsApprovedDigestAsync(_workspace.Root, approvedDigest));

        Assert.False(await store.IsApprovedDigestAsync(_workspace.Root, differentDigest));

    }

    [Fact]
    public async Task IsTrustedAsync_with_source_digest_requires_current_file_and_entry_to_match_approval()
    {

        string mcpPath = _workspace.WriteFile("mcp.json", """{"mcpServers":{"a":{}}}""");

        string digestA = await ComputeSha256HexAsync(mcpPath);

        using TrustedMcpWorkspaceStore store = new();

        await store.TrustAsync(_workspace.Root);

        Assert.True(await store.IsTrustedAsync(_workspace.Root, digestA));

        _workspace.WriteFile("mcp.json", """{"mcpServers":{"b":{}}}""");

        string digestB = await ComputeSha256HexAsync(mcpPath);

        Assert.False(await store.IsTrustedAsync(_workspace.Root, digestA));

        Assert.False(await store.IsTrustedAsync(_workspace.Root, digestB));

    }

    [Fact]
    public async Task IsApprovedDigestAsync_rejects_parsed_A_after_current_B_is_trusted()
    {

        string mcpPath = _workspace.WriteFile("mcp.json", """{"mcpServers":{"a":{}}}""");

        string parsedDigestA = await ComputeSha256HexAsync(mcpPath);

        _workspace.WriteFile("mcp.json", """{"mcpServers":{"b":{}}}""");

        string trustedDigestB = await ComputeSha256HexAsync(mcpPath);

        using TrustedMcpWorkspaceStore store = new();

        await store.TrustAsync(_workspace.Root);

        Assert.False(await store.IsApprovedDigestAsync(_workspace.Root, parsedDigestA));

        Assert.True(await store.IsApprovedDigestAsync(_workspace.Root, trustedDigestB));

    }

    [Fact]
    public async Task TrustAsync_accepts_mcp_json_at_exact_byte_limit()
    {

        byte[] exact = CreateValidMcpConfigBytes(McpSecurityLimits.MaxMcpConfigBytes);

        await File.WriteAllBytesAsync(Path.Combine(_workspace.Root, "mcp.json"), exact);

        using TrustedMcpWorkspaceStore store = new();

        await store.TrustAsync(_workspace.Root);

        Assert.True(await store.IsTrustedAsync(_workspace.Root));

    }

    [Fact]
    public async Task Oversized_mcp_json_fails_closed_for_query_and_explicit_trust()
    {

        byte[] oversized = CreateValidMcpConfigBytes(McpSecurityLimits.MaxMcpConfigBytes + 1);

        await File.WriteAllBytesAsync(Path.Combine(_workspace.Root, "mcp.json"), oversized);

        using TrustedMcpWorkspaceStore store = new();

        Assert.False(await store.IsTrustedAsync(_workspace.Root));

        TrustedMcpWorkspaceStoreException exception =
            await Assert.ThrowsAsync<TrustedMcpWorkspaceStoreException>(
                () => store.TrustAsync(_workspace.Root));

        Assert.Contains("maximum", exception.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(_workspace.Root, exception.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Oversized_trust_document_fails_closed_and_explicit_trust_is_actionable()
    {

        _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        WriteTrustStoreBytes(new byte[TrustedMcpWorkspaceStore.MaxTrustDocumentBytes + 1]);

        using TrustedMcpWorkspaceStore store = new();

        Assert.False(await store.IsTrustedAsync(_workspace.Root));

        TrustedMcpWorkspaceStoreException exception =
            await Assert.ThrowsAsync<TrustedMcpWorkspaceStoreException>(
                () => store.TrustAsync(_workspace.Root));

        Assert.Contains("approval store", exception.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(_storePath, exception.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Corrupt_trust_document_fails_closed_and_is_not_overwritten()
    {

        _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        byte[] corrupt = Encoding.UTF8.GetBytes("""{"entries":""");

        WriteTrustStoreBytes(corrupt);

        using TrustedMcpWorkspaceStore store = new();

        Assert.False(await store.IsTrustedAsync(_workspace.Root));

        await Assert.ThrowsAsync<TrustedMcpWorkspaceStoreException>(
            () => store.TrustAsync(_workspace.Root));

        Assert.Equal(corrupt, await File.ReadAllBytesAsync(_storePath));

    }

    [Fact]
    public async Task Trust_document_with_too_many_entries_fails_closed_and_is_preserved()
    {

        _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        TrustedMcpWorkspaceDocument document = CreateTrustDocument(
            TrustedMcpWorkspaceStore.MaxTrustDocumentEntries + 1);

        byte[] tooMany = JsonSerializer.SerializeToUtf8Bytes(
            document,
            McpConfigJsonSerializerContext.Default.TrustedMcpWorkspaceDocument);

        WriteTrustStoreBytes(tooMany);

        using TrustedMcpWorkspaceStore store = new();

        Assert.False(await store.IsTrustedAsync(_workspace.Root));

        await Assert.ThrowsAsync<TrustedMcpWorkspaceStoreException>(
            () => store.TrustAsync(_workspace.Root));

        Assert.Equal(tooMany, await File.ReadAllBytesAsync(_storePath));

    }

    [Fact]
    public async Task Trust_document_with_malformed_digest_fails_closed()
    {

        _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        TrustedMcpWorkspaceDocument document = new();

        document.Entries[Path.GetFullPath(_workspace.Root)] = "not-a-sha256-digest";

        WriteTrustStoreDocument(document);

        using TrustedMcpWorkspaceStore store = new();

        Assert.False(await store.IsTrustedAsync(_workspace.Root));

        await Assert.ThrowsAsync<TrustedMcpWorkspaceStoreException>(
            () => store.TrustAsync(_workspace.Root));

    }

    [Fact]
    public async Task Trust_document_with_non_normalized_path_fails_closed()
    {

        _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        TrustedMcpWorkspaceDocument document = new();

        document.Entries["relative/workspace"] = new string('A', TrustedMcpWorkspaceStore.Sha256HexLength);

        WriteTrustStoreDocument(document);

        using TrustedMcpWorkspaceStore store = new();

        Assert.False(await store.IsTrustedAsync(_workspace.Root));

        await Assert.ThrowsAsync<TrustedMcpWorkspaceStoreException>(
            () => store.TrustAsync(_workspace.Root));

    }

    [Fact]
    public async Task Trust_document_with_null_entries_fails_closed()
    {

        _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        TrustedMcpWorkspaceDocument document = new() { Entries = null! };

        WriteTrustStoreDocument(document);

        using TrustedMcpWorkspaceStore store = new();

        Assert.False(await store.IsTrustedAsync(_workspace.Root));

    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Trust_document_with_blank_path_fails_closed(string invalidPath)
    {

        _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        TrustedMcpWorkspaceDocument document = new();

        document.Entries[invalidPath] = new string('A', TrustedMcpWorkspaceStore.Sha256HexLength);

        WriteTrustStoreDocument(document);

        using TrustedMcpWorkspaceStore store = new();

        Assert.False(await store.IsTrustedAsync(_workspace.Root));

    }

    [Fact]
    public async Task Trust_document_with_oversized_path_fails_closed()
    {

        _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        TrustedMcpWorkspaceDocument document = new();

        string invalidPath = Path.DirectorySeparatorChar
            + new string('x', TrustedMcpWorkspaceStore.MaxNormalizedWorkspacePathChars);

        document.Entries[invalidPath] = new string('A', TrustedMcpWorkspaceStore.Sha256HexLength);

        WriteTrustStoreDocument(document);

        using TrustedMcpWorkspaceStore store = new();

        Assert.False(await store.IsTrustedAsync(_workspace.Root));

    }

    [Fact]
    public async Task Trust_document_with_null_digest_fails_closed()
    {

        _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        TrustedMcpWorkspaceDocument document = new();

        document.Entries[Path.GetFullPath(_workspace.Root)] = null!;

        WriteTrustStoreDocument(document);

        using TrustedMcpWorkspaceStore store = new();

        Assert.False(await store.IsTrustedAsync(_workspace.Root));

    }

    [Fact]
    public async Task Trust_document_accepts_lowercase_sha256_hex()
    {

        string lowercaseDigest = new('a', TrustedMcpWorkspaceStore.Sha256HexLength);

        TrustedMcpWorkspaceDocument document = new();

        document.Entries[Path.GetFullPath(_workspace.Root)] = lowercaseDigest;

        WriteTrustStoreDocument(document);

        using TrustedMcpWorkspaceStore store = new();

        Assert.True(await store.IsApprovedDigestAsync(_workspace.Root, lowercaseDigest));

    }

    [Fact]
    public async Task IsApprovedDigestAsync_rejects_malformed_candidate_digest()
    {

        _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        using TrustedMcpWorkspaceStore store = new();

        await store.TrustAsync(_workspace.Root);

        Assert.False(await store.IsApprovedDigestAsync(_workspace.Root, "ABC"));

        Assert.False(await store.IsTrustedAsync(
            _workspace.Root,
            new string('G', TrustedMcpWorkspaceStore.Sha256HexLength)));

    }

    [Fact]
    public async Task TrustAsync_preserves_previous_document_when_canceled()
    {

        _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        using TrustedMcpWorkspaceStore store = new();

        await store.TrustAsync(_workspace.Root);

        byte[] previous = await File.ReadAllBytesAsync(_storePath);

        string secondWorkspace = Path.Combine(_workspace.Root, "second");

        Directory.CreateDirectory(secondWorkspace);

        await File.WriteAllTextAsync(
            Path.Combine(secondWorkspace, "mcp.json"),
            """{"mcpServers":{"second":{}}}""");

        using CancellationTokenSource canceled = new();

        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.TrustAsync(secondWorkspace, canceled.Token));

        Assert.Equal(previous, await File.ReadAllBytesAsync(_storePath));

    }

    [Fact]
    public async Task TrustAsync_preserves_full_valid_document_when_new_entry_exceeds_limit()
    {

        _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        TrustedMcpWorkspaceDocument document = CreateTrustDocument(
            TrustedMcpWorkspaceStore.MaxTrustDocumentEntries);

        byte[] full = JsonSerializer.SerializeToUtf8Bytes(
            document,
            McpConfigJsonSerializerContext.Default.TrustedMcpWorkspaceDocument);

        WriteTrustStoreBytes(full);

        using TrustedMcpWorkspaceStore store = new();

        await Assert.ThrowsAsync<TrustedMcpWorkspaceStoreException>(
            () => store.TrustAsync(_workspace.Root));

        Assert.Equal(full, await File.ReadAllBytesAsync(_storePath));

    }

    [Fact]
    public async Task Trust_queries_preserve_cancellation()
    {

        _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        using TrustedMcpWorkspaceStore store = new();

        using CancellationTokenSource canceled = new();

        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.IsTrustedAsync(_workspace.Root, canceled.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.IsApprovedDigestAsync(
                _workspace.Root,
                new string('A', TrustedMcpWorkspaceStore.Sha256HexLength),
                canceled.Token));

    }

    [Fact]
    public async Task TrustAsync_applies_owner_only_permissions()
    {

        _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        using TrustedMcpWorkspaceStore store = new();

        await store.TrustAsync(_workspace.Root);

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        UnixFileMode mode = File.GetUnixFileMode(_storePath);

        UnixFileMode permissionBits =
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute;

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            mode & permissionBits);

    }

    [Fact]
    public async Task TrustAsync_fails_when_strict_directory_permission_verification_fails()
    {

        _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests =
            (_, isDirectory) => isDirectory ? false : true;

        using TrustedMcpWorkspaceStore store = new();

        TrustedMcpWorkspaceStoreException exception =
            await Assert.ThrowsAsync<TrustedMcpWorkspaceStoreException>(
                () => store.TrustAsync(_workspace.Root));

        Assert.Contains("permissions", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(_storePath));

    }

    [Fact]
    public async Task TrustAsync_rolls_back_when_final_file_permission_verification_fails()
    {

        _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        using TrustedMcpWorkspaceStore store = new();

        await store.TrustAsync(_workspace.Root);

        byte[] previous = await File.ReadAllBytesAsync(_storePath);
        string secondWorkspace = Path.Combine(_workspace.Root, "second-workspace");

        Directory.CreateDirectory(secondWorkspace);
        await File.WriteAllTextAsync(
            Path.Combine(secondWorkspace, "mcp.json"),
            """{"mcpServers":{"second":{"type":"sse","url":"https://example.test/mcp"}}}""");

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests =
            (path, isDirectory) =>
                isDirectory
                || !string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(_storePath),
                    StringComparison.Ordinal);

        await Assert.ThrowsAsync<TrustedMcpWorkspaceStoreException>(
            () => store.TrustAsync(secondWorkspace));

        Assert.Equal(previous, await File.ReadAllBytesAsync(_storePath));

    }

    [Fact]
    public void NormalizeWorkspaceRoot_returns_full_path()
    {

        string normalized = TrustedMcpWorkspaceStore.NormalizeWorkspaceRoot(_workspace.Root);

        Assert.Equal(Path.GetFullPath(_workspace.Root), normalized);

    }

    [Fact]
    public void NormalizeWorkspaceRoot_rejects_paths_beyond_internal_limit()
    {

        string oversized = Path.DirectorySeparatorChar
            + new string('x', TrustedMcpWorkspaceStore.MaxNormalizedWorkspacePathChars);

        Assert.Throws<ArgumentException>(
            () => TrustedMcpWorkspaceStore.NormalizeWorkspaceRoot(oversized));

    }

    [Fact]
    public void NormalizeWorkspaceRoot_rejects_relative_path_that_expands_beyond_internal_limit()
    {

        string oversizedAfterNormalization =
            new('x', TrustedMcpWorkspaceStore.MaxNormalizedWorkspacePathChars);

        Assert.Throws<ArgumentException>(
            () => TrustedMcpWorkspaceStore.NormalizeWorkspaceRoot(
                oversizedAfterNormalization));

    }

    private static async Task<string> ComputeSha256HexAsync(string path)
    {

        await using FileStream stream = File.OpenRead(path);

        byte[] hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);

        return Convert.ToHexString(hash);

    }

    private static byte[] CreateValidMcpConfigBytes(int byteCount)
    {

        byte[] json = Encoding.UTF8.GetBytes("""{"mcpServers":{}}""");

        Assert.True(byteCount >= json.Length);

        byte[] bytes = new byte[byteCount];

        json.CopyTo(bytes, 0);

        bytes.AsSpan(json.Length).Fill((byte)' ');

        return bytes;

    }

    private TrustedMcpWorkspaceDocument CreateTrustDocument(int entryCount)
    {

        TrustedMcpWorkspaceDocument document = new();

        for (int i = 0; i < entryCount; i++)
        {

            string path = Path.GetFullPath(Path.Combine(_workspace.Root, $"trusted-{i:D4}"));

            document.Entries[path] = new string('A', TrustedMcpWorkspaceStore.Sha256HexLength);

        }

        return document;

    }

    private void WriteTrustStoreDocument(TrustedMcpWorkspaceDocument document)
    {

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            document,
            McpConfigJsonSerializerContext.Default.TrustedMcpWorkspaceDocument);

        WriteTrustStoreBytes(bytes);

    }

    private void WriteTrustStoreBytes(byte[] bytes)
    {

        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);

        File.WriteAllBytes(_storePath, bytes);

    }

}
