using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The request-path secret-store read behind a digest-cache miss. A Keychain fault mid-process must
/// not turn every header-bearing request into its own blocking secure-storage read.
/// </summary>
[Collection("ProcessEnvironment")]
public sealed class ApiKeyAuthenticatorTests
{
    private const string ApiKey = "authenticator-test-key";

    [Fact]
    public async Task Concurrent_misses_issue_one_store_read()
    {
        GatedSecretStore secretStore = new(SecretStoreReadResult.Ok(ApiKey));

        ApiKeyAuthenticator authenticator = new(
            secretStore,
            new ApiKeyDigestCache(new FakeTimeProvider()),
            NullLogger<ApiKeyAuthenticator>.Instance);

        Task<bool>[] requests = new Task<bool>[20];

        try
        {
            for (int index = 0; index < requests.Length; index++)
            {
                requests[index] = Task.Run(
                    () => authenticator.IsAuthorizedAsync(CreateContext(ApiKey)).AsTask());
            }

            await secretStore.WaitUntilReadIsPendingAsync(TimeSpan.FromSeconds(10));

            // Give every other request the chance to reach the store before the first read finishes.
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            secretStore.ReleaseReads();
        }

        bool[] results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, Assert.True);
        Assert.Equal(1, secretStore.PeekCallCount);
    }

    [Fact]
    public async Task A_failed_read_is_not_retried_per_request()
    {
        GatedSecretStore secretStore = new(
            SecretStoreReadResult.Corrupted("test: OS key storage failed"),
            gated: false);

        FakeTimeProvider time = new();

        ApiKeyDigestCache cache = new(time);

        ApiKeyAuthenticator authenticator = new(
            secretStore,
            cache,
            NullLogger<ApiKeyAuthenticator>.Instance,
            timeProvider: time);

        for (int request = 0; request < 5; request++)
        {
            Assert.False(await authenticator.IsAuthorizedAsync(CreateContext(ApiKey)));

            Assert.False(await authenticator.IsAuthorizedAsync(CreateContext("bogus-key")));
        }

        Assert.Equal(1, secretStore.PeekCallCount);

        // The failure is remembered for a short while only: past it, the next miss reads again.
        time.Advance(ApiKeyAuthenticator.FailedReadRetryDelay);

        Assert.False(await authenticator.IsAuthorizedAsync(CreateContext(ApiKey)));

        Assert.Equal(2, secretStore.PeekCallCount);

        // A rotation (cache invalidation) starts a new generation, which is never answered from an
        // older generation's failure.
        cache.Invalidate();

        Assert.False(await authenticator.IsAuthorizedAsync(CreateContext(ApiKey)));

        Assert.Equal(3, secretStore.PeekCallCount);
    }

    /// <summary>
    /// DESIGN §11.2 item 4: a locked keychain at boot is answered from the (current) encrypted mirror.
    /// That boot is pointless if every client is refused once the startup digest's 30 s TTL lapses
    /// while the keychain is still locked. Peeks keep failing closed; the request path keeps the key
    /// this process adopted at startup until an OS read answers again.
    /// </summary>
    [Fact]
    public async Task A_mirror_served_boot_does_not_401_after_the_ttl_while_the_os_read_fails()
    {
        using ArcanumTestHomeScope home = new("arcanum-auth-mirror-boot");

        IDataProtectionProvider protection = DataProtectionProvider.Create(
            new DirectoryInfo(home.Root),
            _ => { });

        using (DataProtectionSecretStore earlierRun = new(
                   protection,
                   new ApiKeyDigestCache(new FakeTimeProvider())))
        {
            await earlierRun.SaveApiKeyAsync(ApiKey);
        }

        FakeTimeProvider time = new();

        ApiKeyDigestCache cache = new(time);

        LockedKeychainStore os = new();

        using OsKeychainSecretStore store = new(
            os,
            new DataProtectionSecretStore(protection, cache),
            cache,
            NullLogger<OsKeychainSecretStore>.Instance);

        MasterApiKeyBootstrapResult? boot = await ArcanumMasterKeyBootstrapper.PrepareMasterApiKeyAsync(
            store,
            os,
            cache,
            grimoireExists: static () => true);

        Assert.NotNull(boot);
        Assert.False(boot.WasGenerated);

        ApiKeyAuthenticator authenticator = new(
            store,
            cache,
            NullLogger<ApiKeyAuthenticator>.Instance,
            timeProvider: time);

        Assert.True(await authenticator.IsAuthorizedAsync(CreateContext(ApiKey)));

        time.Advance(TimeSpan.FromSeconds(
            ArcanumSettingClamps.ApiKeyCacheTtlSeconds(ArcanumRuntimeDefaults.SecurityApiKeyCacheTtlSeconds) + 1));

        Assert.True(await authenticator.IsAuthorizedAsync(CreateContext(ApiKey)));

        Assert.False(await authenticator.IsAuthorizedAsync(CreateContext("bogus-key")));

        Assert.Equal(
            SecretStoreReadStatus.Corrupted,
            (await store.PeekApiKeyReadResultAsync()).Status);
    }

    /// <summary>
    /// A secret-store read that throws fails closed exactly like an unreadable store, but the fault is
    /// not swallowed: a mirror or keychain fault the store rethrows without logging would otherwise
    /// leave every client refused with nothing on record to say why.
    /// </summary>
    [Fact]
    public async Task A_thrown_store_fault_fails_closed_and_is_logged()
    {
        InvalidOperationException fault = new("test: the keychain read threw");

        ThrowingSecretStore secretStore = new(fault);

        TestCapturingLogger<ApiKeyAuthenticator> logger = new();

        ApiKeyAuthenticator authenticator = new(
            secretStore,
            new ApiKeyDigestCache(new FakeTimeProvider()),
            logger);

        Assert.False(await authenticator.IsAuthorizedAsync(CreateContext(ApiKey)));

        TestLogEntry entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Warning, entry.Level);

        Assert.Same(fault, entry.Exception);
    }

    /// <summary>
    /// ASP.NET Core activates <see cref="ApiKeyEndpointFilter"/> per endpoint. The filter must use the
    /// middleware's singleton authenticator, so a read the middleware just saw fail is remembered for
    /// the filter too, instead of the filter repeating its own secure-storage read for the same request.
    /// </summary>
    [Fact]
    public async Task The_endpoint_filter_shares_the_middleware_authenticator()
    {
        GatedSecretStore secretStore = new(
            SecretStoreReadResult.Corrupted("test: OS key storage failed"),
            gated: false);

        ServiceCollection services = new();

        services.AddLogging();
        services.AddSingleton<ISecretStore>(secretStore);
        services.AddSingleton<IApiKeyDigestCache>(new ApiKeyDigestCache(new FakeTimeProvider()));
        services.AddSingleton<ApiKeyAuthenticator>();

        using ServiceProvider provider = services.BuildServiceProvider();

        ApiKeyAuthenticator middleware = provider.GetRequiredService<ApiKeyAuthenticator>();

        ApiKeyEndpointFilter filter = ActivatorUtilities.CreateInstance<ApiKeyEndpointFilter>(provider);

        DefaultHttpContext request = CreateContext(ApiKey);

        Assert.False(await middleware.IsAuthorizedAsync(request));

        bool nextCalled = false;

        _ = await filter.InvokeAsync(
            new TestEndpointFilterInvocationContext(request),
            _ =>
            {
                nextCalled = true;

                return ValueTask.FromResult<object?>(null);
            });

        Assert.False(nextCalled);

        Assert.Equal(1, secretStore.PeekCallCount);
    }

    private static DefaultHttpContext CreateContext(string apiKey)
    {
        DefaultHttpContext httpContext = new();

        httpContext.Request.Headers[ArcanumApiHeaders.ApiKey] = apiKey;

        return httpContext;
    }

    private sealed class GatedSecretStore(
        SecretStoreReadResult result,
        bool gated = true) : ISecretStore
    {
        private readonly TaskCompletionSource _readPending = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private int _peekCallCount;

        public int PeekCallCount => Volatile.Read(ref _peekCallCount);

        public Task<string?> GetApiKeyAsync() =>
            throw new InvalidOperationException("Authentication must use the non-mutating Peek read.");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            throw new InvalidOperationException("Authentication must use the non-mutating Peek read.");

        public async Task<SecretStoreReadResult> PeekApiKeyReadResultAsync()
        {
            _ = Interlocked.Increment(ref _peekCallCount);

            if (gated)
            {
                _readPending.TrySetResult();

                await _release.Task.ConfigureAwait(false);
            }

            return result;
        }

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

        public Task WaitUntilReadIsPendingAsync(TimeSpan timeout) =>
            _readPending.Task.WaitAsync(timeout);

        public void ReleaseReads() => _release.TrySetResult();
    }

    /// <summary>A reachable backend that refuses every call: a locked macOS keychain.</summary>
    private sealed class LockedKeychainStore : IOsCredentialStore
    {
        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            OsCredentialStoreResult.Failed("test: the keychain is locked");

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            OsCredentialStoreResult.Failed("test: the keychain is locked");

        public OsCredentialStoreResult Delete(string service, string account) =>
            OsCredentialStoreResult.Failed("test: the keychain is locked");
    }

    private sealed class ThrowingSecretStore(Exception fault) : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() =>
            throw new InvalidOperationException("Authentication must use the non-mutating Peek read.");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            throw new InvalidOperationException("Authentication must use the non-mutating Peek read.");

        public Task<SecretStoreReadResult> PeekApiKeyReadResultAsync() => Task.FromException<SecretStoreReadResult>(fault);

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;
    }

    private sealed class TestEndpointFilterInvocationContext(HttpContext httpContext) : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = httpContext;

        public override IList<object?> Arguments { get; } = [];

        public override T GetArgument<T>(int index) => throw new NotSupportedException();
    }
}
