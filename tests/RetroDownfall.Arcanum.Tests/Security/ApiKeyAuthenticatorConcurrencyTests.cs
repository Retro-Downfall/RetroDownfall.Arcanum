using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class ApiKeyAuthenticatorConcurrencyTests
{
    private const string OriginalKey = "original-api-key";

    private const string RotatedKey = "rotated-api-key";

    [Fact]
    public async Task Cache_miss_completed_after_invalidation_is_rejected_and_not_published()
    {
        BlockingSecretStore secretStore = new(OriginalKey);

        ApiKeyDigestCache cache = new(new FakeTimeProvider());

        ApiKeyAuthenticator authenticator = new(secretStore, cache, NullLogger<ApiKeyAuthenticator>.Instance);

        DefaultHttpContext httpContext = new();

        httpContext.Request.Headers[ArcanumApiHeaders.ApiKey] = OriginalKey;

        Task<bool> authentication = authenticator.IsAuthorizedAsync(httpContext).AsTask();

        try
        {
            await secretStore.WaitUntilReadIsPendingAsync(TimeSpan.FromSeconds(10));

            secretStore.ApiKey = RotatedKey;

            cache.Invalidate();
        }
        finally
        {
            secretStore.ReleasePendingRead();
        }

        Assert.False(await authentication.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, secretStore.PeekCallCount);
        Assert.False(cache.TryGetDigest(out _));
        Assert.False(cache.TryGetPresenceDigest(out _));
    }

    [Fact]
    public async Task Failed_cache_miss_reuses_a_concurrently_published_winner()
    {
        FailingFirstSecretStore secretStore = new(OriginalKey);
        ApiKeyDigestCache cache = new(new FakeTimeProvider());
        ApiKeyAuthenticator authenticator = new(secretStore, cache, NullLogger<ApiKeyAuthenticator>.Instance);

        Task<bool> failedReader = authenticator
            .IsAuthorizedAsync(CreateContext(OriginalKey))
            .AsTask();

        await secretStore.WaitUntilFirstReadIsPendingAsync(TimeSpan.FromSeconds(10));

        // A miss joins the read in flight for the generation it observed, so the concurrent winner
        // comes from a newer generation (an invalidation), whose own read succeeds.
        cache.Invalidate();

        bool winner = await authenticator
            .IsAuthorizedAsync(CreateContext(OriginalKey))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            Assert.True(winner);
        }
        finally
        {
            secretStore.ReleaseFirstRead();
        }

        Assert.True(await failedReader.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, secretStore.PeekCallCount);
    }

    [Fact]
    public async Task Stale_cache_miss_cannot_replace_or_authenticate_against_a_rotated_winner()
    {
        BlockingSecretStore secretStore = new(OriginalKey);

        ApiKeyDigestCache cache = new(new FakeTimeProvider());

        ApiKeyAuthenticator authenticator = new(secretStore, cache, NullLogger<ApiKeyAuthenticator>.Instance);

        Task<bool> staleAuthentication = authenticator
            .IsAuthorizedAsync(CreateContext(OriginalKey))
            .AsTask();

        bool rotatedAuthorized = false;

        try
        {
            await secretStore.WaitUntilReadIsPendingAsync(TimeSpan.FromSeconds(10));

            secretStore.ApiKey = RotatedKey;

            cache.Invalidate();

            rotatedAuthorized = await authenticator
                .IsAuthorizedAsync(CreateContext(RotatedKey))
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            secretStore.ReleasePendingRead();
        }

        Assert.True(rotatedAuthorized);
        Assert.False(await staleAuthentication.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, secretStore.PeekCallCount);

        Assert.True(cache.TryGetDigest(out byte[]? currentDigest));

        Assert.True(cache.TryGetPresenceDigest(out byte[]? presenceDigest));

        byte[] rotatedKeyUtf8 = Encoding.UTF8.GetBytes(RotatedKey);

        byte[] expectedDigest = SHA256.HashData(rotatedKeyUtf8);

        try
        {
            Assert.Equal(expectedDigest, currentDigest);
            Assert.Equal(expectedDigest, presenceDigest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rotatedKeyUtf8);
            CryptographicOperations.ZeroMemory(expectedDigest);
            CryptographicOperations.ZeroMemory(currentDigest!);
            CryptographicOperations.ZeroMemory(presenceDigest!);
        }
    }

    private static DefaultHttpContext CreateContext(string apiKey)
    {
        DefaultHttpContext httpContext = new();

        httpContext.Request.Headers[ArcanumApiHeaders.ApiKey] = apiKey;

        return httpContext;
    }

    private sealed class BlockingSecretStore(string apiKey) : ISecretStore
    {
        private readonly TaskCompletionSource _readPending = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _readRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private int _peekCallCount;

        public string ApiKey { get; set; } = apiKey;

        public int PeekCallCount => Volatile.Read(ref _peekCallCount);

        public Task<string?> GetApiKeyAsync() =>
            throw new InvalidOperationException("Authentication must use the non-mutating Peek read.");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            throw new InvalidOperationException("Authentication must use the non-mutating Peek read.");

        public async Task<SecretStoreReadResult> PeekApiKeyReadResultAsync()
        {
            string capturedApiKey = ApiKey;

            int readNumber = Interlocked.Increment(ref _peekCallCount);

            if (readNumber == 1)
            {
                _readPending.TrySetResult();

                await _readRelease.Task.ConfigureAwait(false);
            }

            return SecretStoreReadResult.Ok(capturedApiKey);
        }

        public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

        public Task WaitUntilReadIsPendingAsync(TimeSpan timeout) =>
            _readPending.Task.WaitAsync(timeout);

        public void ReleasePendingRead() =>
            _readRelease.TrySetResult();
    }

    private sealed class FailingFirstSecretStore(string apiKey) : ISecretStore
    {
        private readonly TaskCompletionSource _firstReadPending = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _firstReadRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private int _peekCallCount;

        internal int PeekCallCount => Volatile.Read(ref _peekCallCount);

        public Task<string?> GetApiKeyAsync() =>
            throw new InvalidOperationException("Authentication must use the non-mutating Peek read.");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            throw new InvalidOperationException("Authentication must use the non-mutating Peek read.");

        public async Task<SecretStoreReadResult> PeekApiKeyReadResultAsync()
        {
            if (Interlocked.Increment(ref _peekCallCount) != 1)
            {
                return SecretStoreReadResult.Ok(apiKey);
            }

            _firstReadPending.TrySetResult();
            await _firstReadRelease.Task.ConfigureAwait(false);

            return SecretStoreReadResult.Missing();
        }

        public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

        internal Task WaitUntilFirstReadIsPendingAsync(TimeSpan timeout) =>
            _firstReadPending.Task.WaitAsync(timeout);

        internal void ReleaseFirstRead() => _firstReadRelease.TrySetResult();
    }
}
