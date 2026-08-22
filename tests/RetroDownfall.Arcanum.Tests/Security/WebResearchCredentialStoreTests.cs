using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

[Collection("ProcessEnvironment")]
public sealed class WebResearchCredentialStoreTests : IDisposable
{
    private readonly string _testHome =
        Path.Combine(
            Path.GetTempPath(),
            $"arcanum-web-credential-{Guid.NewGuid():N}");

    private readonly Dictionary<string, string?> _originalEnvironment = new();
    private readonly IDataProtectionProvider _dataProtectionProvider;

    public WebResearchCredentialStoreTests()
    {
        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Testing");
        SetEnvironment("DOTNET_ENVIRONMENT", "Testing");
        SetEnvironment("ARCANUM_TEST_HOME", _testHome);

        Directory.CreateDirectory(_testHome);
        _dataProtectionProvider = DataProtectionProvider.Create(
            new DirectoryInfo(_testHome),
            _ => { });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testHome))
            {
                Directory.Delete(_testHome, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
        finally
        {
            foreach (KeyValuePair<string, string?> entry in _originalEnvironment)
            {
                global::System.Environment.SetEnvironmentVariable(
                    entry.Key,
                    entry.Value);
            }
        }
    }

    [Fact]
    public async Task Save_and_get_use_os_store_and_encrypted_fallback()
    {
        InMemoryOsCredentialStore os = new();
        using WebResearchCredentialStore store = CreateStore(os);

        await store.SavePerplexityApiKeyAsync("pplx-secret-value");

        SecretStoreReadResult result =
            await store.GetPerplexityApiKeyReadResultAsync();
        OsCredentialStoreResult direct = os.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.PerplexityApiKeyAccount);

        Assert.Equal(SecretStoreReadStatus.Ok, result.Status);
        Assert.Equal("pplx-secret-value", result.Value);
        Assert.Equal("pplx-secret-value", direct.Value);
        Assert.True(File.Exists(ArcanumPaths.PerplexityApiKeyStoreFile));

        byte[] protectedBytes =
            await File.ReadAllBytesAsync(ArcanumPaths.PerplexityApiKeyStoreFile);
        Assert.DoesNotContain(
            "pplx-secret-value",
            Encoding.UTF8.GetString(protectedBytes),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unavailable_os_store_round_trips_through_data_protection()
    {
        using WebResearchCredentialStore writer = CreateStore(new UnavailableStore());
        await writer.SavePerplexityApiKeyAsync("fallback-secret");

        using WebResearchCredentialStore reader = CreateStore(new UnavailableStore());
        SecretStoreReadResult result =
            await reader.GetPerplexityApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Ok, result.Status);
        Assert.Equal("fallback-secret", result.Value);
    }

    [Fact]
    public async Task Get_migrates_existing_fallback_into_os_store()
    {
        using (WebResearchCredentialStore writer =
               CreateStore(new UnavailableStore()))
        {
            await writer.SavePerplexityApiKeyAsync("migration-secret");
        }

        InMemoryOsCredentialStore os = new();
        using WebResearchCredentialStore reader = CreateStore(os);

        SecretStoreReadResult result =
            await reader.GetPerplexityApiKeyReadResultAsync();
        OsCredentialStoreResult migrated = os.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.PerplexityApiKeyAccount);

        Assert.Equal("migration-secret", result.Value);
        Assert.Equal(OsCredentialStoreStatus.Ok, migrated.Status);
        Assert.Equal("migration-secret", migrated.Value);
    }

    [Fact]
    public async Task Peek_returns_fallback_without_promoting_or_changing_files()
    {
        using (WebResearchCredentialStore writer = CreateStore(new UnavailableStore()))
        {
            await writer.SavePerplexityApiKeyAsync("peek-web-secret");
        }

        string[] before = SnapshotFileTree();
        RecordingOsCredentialStore os = new(OsCredentialStoreResult.NotFound());
        using WebResearchCredentialStore store = CreateStore(os);
        IWebResearchCredentialStore contract = store;

        SecretStoreReadResult first =
            await contract.PeekPerplexityApiKeyReadResultAsync();
        SecretStoreReadResult second =
            await contract.PeekPerplexityApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Ok, first.Status);
        Assert.Equal("peek-web-secret", first.Value);
        Assert.Equal(first, second);
        Assert.Equal(0, os.SetCallCount);
        Assert.Equal(0, os.DeleteCallCount);
        Assert.Equal(before, SnapshotFileTree());
    }

    [Fact]
    public async Task Peek_fails_closed_when_os_read_is_ambiguous_even_with_valid_fallback()
    {
        using (WebResearchCredentialStore writer = CreateStore(new UnavailableStore()))
        {
            await writer.SavePerplexityApiKeyAsync("possibly-superseded-web-secret");
        }

        string[] before = SnapshotFileTree();
        RecordingOsCredentialStore os = new(
            OsCredentialStoreResult.Failed("test ambiguous read"));
        using WebResearchCredentialStore store = CreateStore(os);

        SecretStoreReadResult result = await ((IWebResearchCredentialStore)store)
            .PeekPerplexityApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Corrupted, result.Status);
        Assert.Null(result.Value);
        Assert.Equal(0, os.SetCallCount);
        Assert.Equal(0, os.DeleteCallCount);
        Assert.Equal(before, SnapshotFileTree());
    }

    [Fact]
    public async Task Peek_reports_missing_and_corrupt_fallbacks_without_writing_state()
    {
        RecordingOsCredentialStore os = new(OsCredentialStoreResult.NotFound());
        using WebResearchCredentialStore store = CreateStore(os);
        string[] missingBefore = SnapshotFileTree();

        SecretStoreReadResult missing = await ((IWebResearchCredentialStore)store)
            .PeekPerplexityApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Missing, missing.Status);
        Assert.Equal(missingBefore, SnapshotFileTree());

        string path = ArcanumPaths.PerplexityApiKeyStoreFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        string[] corruptBefore = SnapshotFileTree();

        SecretStoreReadResult corrupt = await ((IWebResearchCredentialStore)store)
            .PeekPerplexityApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Corrupted, corrupt.Status);
        Assert.Equal(corruptBefore, SnapshotFileTree());
        Assert.Equal(0, os.SetCallCount);
        Assert.Equal(0, os.DeleteCallCount);
    }

    [Fact]
    public async Task Delete_removes_os_and_fallback_copies()
    {
        InMemoryOsCredentialStore os = new();
        using WebResearchCredentialStore store = CreateStore(os);
        await store.SavePerplexityApiKeyAsync("delete-me");

        await store.DeletePerplexityApiKeyAsync();

        Assert.False(File.Exists(ArcanumPaths.PerplexityApiKeyStoreFile));
        Assert.Equal(
            OsCredentialStoreStatus.NotFound,
            os.TryGet(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.PerplexityApiKeyAccount).Status);
        Assert.Equal(
            SecretStoreReadStatus.Missing,
            (await store.GetPerplexityApiKeyReadResultAsync()).Status);
    }

    [Fact]
    public async Task A_failed_os_write_removes_the_superseded_os_credential()
    {
        InMemoryOsCredentialStore backing = new();
        _ = backing.Set(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.PerplexityApiKeyAccount,
            "pplx-old");
        using WebResearchCredentialStore store = CreateStore(new WriteFailingStore(backing));

        await store.SavePerplexityApiKeyAsync("pplx-new");

        Assert.Equal(
            "pplx-new",
            (await store.GetPerplexityApiKeyReadResultAsync()).Value);
        Assert.Equal(
            OsCredentialStoreStatus.NotFound,
            backing.TryGet(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.PerplexityApiKeyAccount).Status);
    }

    [Fact]
    public async Task A_failed_os_write_fails_closed_when_the_superseded_credential_survives()
    {
        InMemoryOsCredentialStore backing = new();
        _ = backing.Set(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.PerplexityApiKeyAccount,
            "pplx-old");
        using WebResearchCredentialStore store =
            CreateStore(new WriteFailingStore(backing, deleteFails: true));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SavePerplexityApiKeyAsync("pplx-new"));

        Assert.Equal(
            "pplx-old",
            (await store.GetPerplexityApiKeyReadResultAsync()).Value);
        Assert.False(File.Exists(ArcanumPaths.PerplexityApiKeyStoreFile));
    }

    [Fact]
    public async Task Corrupt_fallback_is_reported_without_throwing()
    {
        string path = ArcanumPaths.PerplexityApiKeyStoreFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        using WebResearchCredentialStore store = CreateStore(new UnavailableStore());

        SecretStoreReadResult result =
            await store.GetPerplexityApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Corrupted, result.Status);
        Assert.Null(result.Value);
        Assert.Contains("could not be decrypted", result.Message);
    }

    [Fact]
    public async Task Oversized_fallback_is_reported_without_unbounded_read()
    {

        string path = ArcanumPaths.PerplexityApiKeyStoreFile;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {

            stream.SetLength(WebResearchCredentialStore.MaxProtectedSecretBytes + 1L);

        }

        using WebResearchCredentialStore store = CreateStore(new UnavailableStore());

        SecretStoreReadResult result = await store.GetPerplexityApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Corrupted, result.Status);

    }

    [Fact]
    public async Task Operations_honor_caller_cancellation()
    {
        using WebResearchCredentialStore store =
            CreateStore(new InMemoryOsCredentialStore());
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.GetPerplexityApiKeyReadResultAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SavePerplexityApiKeyAsync("secret", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.DeletePerplexityApiKeyAsync(cancellation.Token));
    }

    private WebResearchCredentialStore CreateStore(IOsCredentialStore os) =>
        new(
            os,
            _dataProtectionProvider,
            NullLogger<WebResearchCredentialStore>.Instance);

    private void SetEnvironment(string name, string value)
    {
        _originalEnvironment[name] =
            global::System.Environment.GetEnvironmentVariable(name);
        global::System.Environment.SetEnvironmentVariable(name, value);
    }

    private string[] SnapshotFileTree() => Directory
        .EnumerateFiles(_testHome, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(path =>
            Path.GetRelativePath(_testHome, path)
            + "|"
            + File.GetLastWriteTimeUtc(path).Ticks
            + "|"
            + Convert.ToBase64String(File.ReadAllBytes(path)))
        .ToArray();

    private sealed class RecordingOsCredentialStore(OsCredentialStoreResult readResult)
        : IOsCredentialStore
    {
        public bool IsAvailable => readResult.Status != OsCredentialStoreStatus.Unavailable;

        public int SetCallCount { get; private set; }

        public int DeleteCallCount { get; private set; }

        public OsCredentialStoreResult TryGet(string service, string account) => readResult;

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {
            SetCallCount++;

            return OsCredentialStoreResult.Ok(secret);
        }

        public OsCredentialStoreResult Delete(string service, string account)
        {
            DeleteCallCount++;

            return OsCredentialStoreResult.Ok(string.Empty);
        }
    }

    /// <summary>
    /// A reachable OS credential backend that refuses writes (locked keychain, transient Secret
    /// Service error) while reads and deletes still work.
    /// </summary>
    private sealed class WriteFailingStore(IOsCredentialStore inner, bool deleteFails = false)
        : IOsCredentialStore
    {
        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            inner.TryGet(service, account);

        public OsCredentialStoreResult Set(
            string service,
            string account,
            string secret) =>
            OsCredentialStoreResult.Failed("test write failure");

        public OsCredentialStoreResult Delete(string service, string account) =>
            deleteFails
                ? OsCredentialStoreResult.Failed("test delete failure")
                : inner.Delete(service, account);
    }

    private sealed class UnavailableStore : IOsCredentialStore
    {
        public bool IsAvailable => false;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            OsCredentialStoreResult.Unavailable("test unavailable");

        public OsCredentialStoreResult Set(
            string service,
            string account,
            string secret) =>
            OsCredentialStoreResult.Unavailable("test unavailable");

        public OsCredentialStoreResult Delete(string service, string account) =>
            OsCredentialStoreResult.Unavailable("test unavailable");
    }
}
