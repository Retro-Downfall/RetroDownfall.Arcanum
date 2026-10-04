using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The request-path secret-store read behind a digest-cache miss. A Keychain fault mid-process must
/// not turn every header-bearing request into its own blocking secure-storage read.
/// </summary>
public sealed class ApiKeyAuthenticatorTests
{
    private const string ApiKey = "authenticator-test-key";

    [Fact]
    public async Task Concurrent_misses_issue_one_store_read()
    {
        GatedSecretStore secretStore = new(SecretStoreReadResult.Ok(ApiKey));

        ApiKeyAuthenticator authenticator = new(
            secretStore,
            new ApiKeyDigestCache(new FakeTimeProvider()));

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
}
