using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

[Collection("ProcessEnvironment")]
public sealed class ProviderCredentialIdentityTests : IDisposable
{
    private const string Variable = "ARCANUM_TEST_CREDENTIAL_IDENTITY";

    private readonly string? _original = global::System.Environment.GetEnvironmentVariable(Variable);

    public ProviderCredentialIdentityTests() => global::System.Environment.SetEnvironmentVariable(Variable, null);

    public void Dispose() => global::System.Environment.SetEnvironmentVariable(Variable, _original);

    private static ProviderSettings Provider() => new() { Name = "identity", CredentialEnvironmentVariable = Variable };

    [Fact]
    public async Task Same_read_binds_secret_and_opaque_stamp_and_currentness_only_peeks()
    {
        Store store = new() { Result = SecretStoreReadResult.Ok("first-secret") };

        IProviderApiKeyResolver resolver = new ProviderApiKeyResolver(store);

        ResolvedProviderCredential capture = await resolver.ResolveWithIdentityAsync(Provider());

        Assert.Equal("first-secret", capture.Secret);

        Assert.NotNull(capture.Stamp);

        Assert.Equal(1, store.Reads);

        Assert.True(await resolver.IsCurrentAsync(Provider(), capture.Stamp));

        Assert.Equal(1, store.Reads);

        Assert.Equal(1, store.Peeks);

        Assert.DoesNotContain("first-secret", capture.ToString());

        store.Result = SecretStoreReadResult.Ok("second-secret");

        Assert.False(await resolver.IsCurrentAsync(Provider(), capture.Stamp));

        store.Result = SecretStoreReadResult.Ok("first-secret");

        ResolvedProviderCredential returned = await resolver.ResolveWithIdentityAsync(Provider());

        Assert.NotEqual(capture.Stamp, returned.Stamp);

        Assert.True(returned.Stamp!.Generation > capture.Stamp.Generation);
    }

    [Fact]
    public async Task Known_keyless_is_provable_but_ambiguous_reads_revoke_prior_proof()
    {
        Store store = new() { Result = SecretStoreReadResult.Missing() };

        IProviderApiKeyResolver resolver = new ProviderApiKeyResolver(store);

        ResolvedProviderCredential keyless = await resolver.ResolveWithIdentityAsync(Provider());

        Assert.Null(keyless.Secret);

        Assert.NotNull(keyless.Stamp);

        Assert.True(await resolver.IsCurrentAsync(Provider(), keyless.Stamp));

        foreach (SecretStoreReadResult failed in new[] { SecretStoreReadResult.Unreadable("failed"), SecretStoreReadResult.Corrupted("failed"), SecretStoreReadResult.Ok(" ") })
        {
            store.Result = failed;

            ResolvedProviderCredential uncertain = await resolver.ResolveWithIdentityAsync(Provider());

            Assert.Null(uncertain.Secret);

            Assert.Null(uncertain.Stamp);
        }

        store.Result = SecretStoreReadResult.Missing();

        Assert.False(await resolver.IsCurrentAsync(Provider(), keyless.Stamp));
    }

    [Fact]
    public async Task Environment_precedence_never_reads_store_and_observed_ABA_never_revives_stamp()
    {
        Store store = new() { Result = SecretStoreReadResult.Ok("store-secret") };

        IProviderApiKeyResolver resolver = new ProviderApiKeyResolver(store);

        global::System.Environment.SetEnvironmentVariable(Variable, "environment-a");

        ResolvedProviderCredential first = await resolver.ResolveWithIdentityAsync(Provider());

        Assert.Equal("environment-a", first.Secret);

        Assert.NotNull(first.Stamp);

        global::System.Environment.SetEnvironmentVariable(Variable, "environment-b");

        Assert.False(await resolver.IsCurrentAsync(Provider(), first.Stamp));

        global::System.Environment.SetEnvironmentVariable(Variable, "environment-a");

        Assert.False(await resolver.IsCurrentAsync(Provider(), first.Stamp));

        Assert.Equal(0, store.Reads + store.Peeks);
    }

    [Fact]
    public async Task Unobserved_external_ABA_is_not_claimed_detectable_and_compatibility_proof_is_absent()
    {
        IProviderApiKeyResolver resolver = new ProviderApiKeyResolver(new Store { Result = SecretStoreReadResult.Ok("a") });

        global::System.Environment.SetEnvironmentVariable(Variable, "a");

        ResolvedProviderCredential first = await resolver.ResolveWithIdentityAsync(Provider());

        global::System.Environment.SetEnvironmentVariable(Variable, "b");

        global::System.Environment.SetEnvironmentVariable(Variable, "a");

        Assert.NotNull(first.Stamp);

        Assert.True(await resolver.IsCurrentAsync(Provider(), first.Stamp));

        IProviderApiKeyResolver compatibility = new LegacyResolver();

        ResolvedProviderCredential legacy = await compatibility.ResolveWithIdentityAsync(Provider());

        Assert.Equal("legacy", legacy.Secret);

        Assert.Null(legacy.Stamp);

        Assert.False(await compatibility.IsCurrentAsync(Provider(), first.Stamp));
    }

    private sealed class LegacyResolver : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>("legacy");
    }

    private sealed class Store : IProviderCredentialStore
    {
        public SecretStoreReadResult Result { get; set; } = SecretStoreReadResult.Missing();

        public int Reads { get; private set; }

        public int Peeks { get; private set; }

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync(string providerName, CancellationToken cancellationToken = default)
        {
            Reads++;

            return Task.FromResult(Result);
        }

        public Task<SecretStoreReadResult> PeekApiKeyReadResultAsync(string providerName, CancellationToken cancellationToken = default)
        {
            Peeks++;

            return Task.FromResult(Result);
        }

        public Task SaveApiKeyAsync(string providerName, string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteApiKeyAsync(string providerName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
