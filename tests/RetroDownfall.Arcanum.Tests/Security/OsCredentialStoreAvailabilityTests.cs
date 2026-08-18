using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// <see cref="IOsCredentialStore.IsAvailable"/> answers "a credential backend answers", never merely
/// "this platform is supported".
/// </summary>
/// <remarks>
/// Three credential stores pick between failing closed and degrading to the encrypted mirror on this
/// one property — <c>OsKeychainSecretStore</c>, <c>ProviderCredentialStore</c> and
/// <c>WebResearchCredentialStore</c> all consult it from <c>PurgeSupersededOsCredential</c>. A store
/// that reports a backend as reachable without ever asking one turns the documented headless
/// fallback (§11.2 item 4) into a thrown save on exactly the hosts that fallback exists for: a Linux
/// box where libsecret loads and no Secret Service is on the bus answers "available", so the save
/// throws instead of writing the mirror and the credential lands nowhere at all.
/// </remarks>
public sealed class OsCredentialStoreAvailabilityTests
{

    /// <summary>
    /// The backend reporting that nothing answered is the strongest evidence there is, and it must
    /// outrank the store's own optimistic platform answer.
    /// </summary>
    [Theory]
    [InlineData("get")]
    [InlineData("set")]
    [InlineData("delete")]
    public void Availability_is_false_once_the_backend_reports_it_cannot_be_reached(string operation)
    {

        OptimisticBackend backend = new()
        {

            Outcome = OsCredentialStoreResult.Unavailable("no secret service answered"),

        };

        OsCredentialStore store = new(backend);

        Invoke(store, operation);

        Assert.False(store.IsAvailable);

    }

    /// <summary>
    /// A locked keychain or an ACL denial is a backend that answered, so the fail-closed branch the
    /// purge relies on must stay reachable. Only "nothing answered" degrades to the mirror.
    /// </summary>
    [Fact]
    public void Availability_survives_a_reachable_backend_that_merely_refuses()
    {

        OptimisticBackend backend = new()
        {

            Outcome = OsCredentialStoreResult.Failed("the keychain is locked"),

        };

        OsCredentialStore store = new(backend);

        _ = store.Set("arcanum", "master-api-key", "sk-new");

        Assert.True(store.IsAvailable);

    }

    /// <summary>
    /// A failure leaves reachability unknown rather than proven, so the next question goes back to
    /// the backend instead of being answered from a remembered verdict.
    /// </summary>
    [Fact]
    public void An_ambiguous_failure_sends_the_next_question_back_to_the_backend()
    {

        OptimisticBackend backend = new()
        {

            Outcome = OsCredentialStoreResult.Failed("transient libsecret error"),

        };

        OsCredentialStore store = new(backend);

        _ = store.Set("arcanum", "master-api-key", "sk-new");

        backend.Reachable = false;

        Assert.False(store.IsAvailable);

    }

    /// <summary>
    /// A keyring daemon that starts after the process did must not leave the store permanently
    /// degraded, so a backend that answers again is believed again.
    /// </summary>
    [Fact]
    public void Availability_returns_once_the_backend_answers_again()
    {

        OptimisticBackend backend = new()
        {

            Outcome = OsCredentialStoreResult.Unavailable("no secret service answered"),

        };

        OsCredentialStore store = new(backend);

        _ = store.Set("arcanum", "master-api-key", "sk-new");

        Assert.False(store.IsAvailable);

        backend.Outcome = OsCredentialStoreResult.Ok("sk-new");

        _ = store.Set("arcanum", "master-api-key", "sk-new");

        Assert.True(store.IsAvailable);

    }

    /// <summary>
    /// Before anything has talked to the backend the store has nothing to report of its own, so the
    /// answer is the backend's.
    /// </summary>
    [Fact]
    public void Nothing_is_assumed_before_the_backend_has_been_asked()
    {

        OptimisticBackend backend = new()
        {

            Reachable = false,

        };

        OsCredentialStore store = new(backend);

        Assert.False(store.IsAvailable);

    }

    private static void Invoke(IOsCredentialStore store, string operation)
    {

        switch (operation)
        {

            case "get":

                _ = store.TryGet("arcanum", "master-api-key");

                return;

            case "set":

                _ = store.Set("arcanum", "master-api-key", "sk-new");

                return;

            default:

                _ = store.Delete("arcanum", "master-api-key");

                return;

        }

    }

    /// <summary>
    /// A backend whose client library is present — so its own availability answer is the optimistic
    /// "this platform is supported" — while every operation reports what actually happened.
    /// </summary>
    /// <remarks>
    /// This is the headless Linux shape the real store produces: <c>libsecret-1.so.0</c> loads and
    /// <c>secret_schema_new</c> succeeds, and no Secret Service is on the bus to answer anything.
    /// </remarks>
    private sealed class OptimisticBackend : IOsCredentialStore
    {

        internal OsCredentialStoreResult Outcome { get; set; } =
            OsCredentialStoreResult.Unavailable("no secret service answered");

        internal bool Reachable { get; set; } = true;

        public bool IsAvailable => Reachable;

        public OsCredentialStoreResult TryGet(string service, string account) => Outcome;

        public OsCredentialStoreResult Set(string service, string account, string secret) => Outcome;

        public OsCredentialStoreResult Delete(string service, string account) => Outcome;

    }

}
