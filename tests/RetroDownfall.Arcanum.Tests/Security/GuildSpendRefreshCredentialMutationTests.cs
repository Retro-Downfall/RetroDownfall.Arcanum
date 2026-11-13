using Microsoft.AspNetCore.DataProtection;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Guild;

namespace RetroDownfall.Arcanum.Tests.Security;

[Collection("ProcessEnvironment")]
public sealed class GuildSpendRefreshCredentialMutationTests
{
    [Fact]
    public Task Pending_local_credential_write_may_follow_refresh_acceptance() => RunAsync(failDelete: false);

    [Fact]
    public Task Failed_local_delete_after_mutation_refuses_refresh_application() => RunAsync(failDelete: true);

    [Fact]
    public Task Completed_mutation_between_credential_checks_refuses_refresh() => RunAsync(failDelete: false, betweenChecks: true);

    private static async Task RunAsync(bool failDelete, bool betweenChecks = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "arcanum-refresh-credentials-" + Guid.NewGuid().ToString("N"));

        string? oldHome = global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        string? oldKey = global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_PENDING_GUILD_REFRESH");

        Directory.CreateDirectory(root);

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", root);

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_PENDING_GUILD_REFRESH", null);

        try
        {
            using BlockingMutationStore os = new();

            using ProviderCredentialStore store = new(os, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "protection"))));

            await store.SaveApiKeyAsync("router", "private-provider-key-sentinel");

            ProviderApiKeyResolver resolver = new(store);

            long initialGeneration = store.MutationGeneration("router");

            IProviderApiKeyResolver effectiveResolver = betweenChecks
                ? new MutateAfterFirstCheckResolver(resolver, () => store.DeleteApiKeyAsync("router").GetAwaiter().GetResult())
                : resolver;

            await GuildQuestStoreTests.AssertCredentialMutationRefusesRefreshAsync(effectiveResolver, async (provider, captured) =>
            {
                if (betweenChecks) return () => Task.CompletedTask;

                if (failDelete)
                {
                    os.FailDeleteAfterMutation = true;

                    InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteApiKeyAsync("router"));

                    Assert.Contains("could not delete", error.Message);

                    Assert.True(store.MutationGeneration("router") > initialGeneration);

                    Assert.NotEqual(ProviderCredentialCurrentness.Current,
                        resolver.GetObservedLocalCurrentness(provider, captured.Stamp!));

                    return () => Task.CompletedTask;
                }

                os.BlockAfterNextSet = true;

                Task pending = Task.Run(() => store.SaveApiKeyAsync("router", "replacement-secret"));

                Task winner = await Task.WhenAny(pending, os.Entered.Task);

                if (winner == pending)
                {
                    await pending;

                    throw new InvalidOperationException("The credential write finished before its barrier was reached.");
                }

                return async () =>
                {
                    os.Release.Set();

                    await pending;
                };
            }, expectRefusal: failDelete || betweenChecks);
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_PENDING_GUILD_REFRESH", oldKey);

            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", oldHome);

            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MutateAfterFirstCheckResolver(ProviderApiKeyResolver inner, Action mutate) : IProviderApiKeyResolver
    {
        private bool _mutated;

        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken ct = default) => inner.ResolveAsync(provider, ct);

        public Task<ResolvedProviderCredential> PeekWithIdentityAsync(ProviderSettings provider, CancellationToken ct = default) =>
            inner.PeekWithIdentityAsync(provider, ct);

        public Task<ProviderCredentialCurrentness> GetCurrentnessAsync(ProviderSettings provider, ProviderCredentialStamp stamp,
            CancellationToken ct = default) => inner.GetCurrentnessAsync(provider, stamp, ct);

        public ProviderCredentialCurrentness GetObservedLocalCurrentness(ProviderSettings provider, ProviderCredentialStamp stamp)
        {
            ProviderCredentialCurrentness result = inner.GetObservedLocalCurrentness(provider, stamp);

            if (!_mutated)
            {
                _mutated = true;

                mutate();
            }

            return result;
        }
    }

    private sealed class BlockingMutationStore : IOsCredentialStore, IOsCredentialPresenceProbe, IDisposable
    {
        private readonly InMemoryOsCredentialStore _inner = new();

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ManualResetEventSlim Release { get; } = new();

        internal bool BlockAfterNextSet { get; set; }

        internal bool FailDeleteAfterMutation { get; set; }

        public bool IsAvailable => true;

        public OsCredentialStoreStatus ProbePresence(string service, string account) => _inner.ProbePresence(service, account);

        public OsCredentialStoreResult TryGet(string service, string account) => _inner.TryGet(service, account);

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {
            OsCredentialStoreResult result = _inner.Set(service, account, secret);

            if (BlockAfterNextSet)
            {
                BlockAfterNextSet = false;

                Entered.TrySetResult();

                Release.Wait();
            }

            return result;
        }

        public OsCredentialStoreResult Delete(string service, string account)
        {
            OsCredentialStoreResult result = _inner.Delete(service, account);

            return FailDeleteAfterMutation ? OsCredentialStoreResult.Failed("injected deletion failure") : result;
        }

        public void Dispose() => Release.Dispose();
    }
}
