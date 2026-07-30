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
