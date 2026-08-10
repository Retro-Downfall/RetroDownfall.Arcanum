using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// Exercises the real platform credential backend, not <see cref="InMemoryOsCredentialStore"/>.
/// </summary>
/// <remarks>
/// The Windows store shipped for months writing uninitialized heap instead of the secret: it
/// allocated the credential blob and handed the pointer to <c>CredWriteW</c> without ever copying
/// the secret in. Nothing failed loudly — <c>CredWriteW</c> succeeded, and only a later read
/// returned garbage. Two gaps let it survive: the suite covered only the in-memory fake, and no CI
/// job ran the suite on Windows at all, so every Windows-gated test was inert everywhere.
/// A round trip through the genuine backend is the assertion that would have caught it.
/// <para>
/// Deliberately platform-agnostic rather than Windows-only. Each OS reaches a different
/// implementation through the same contract, and all three deserve the same pin.
/// </para>
/// <para>
/// Opt-in via <c>ARCANUM_TEST_OS_CREDENTIAL_STORE=true</c>, and skipped otherwise. Every other test
/// in this suite reaches the credential layer through <see cref="InMemoryOsCredentialStore"/>
/// precisely so that running the tests never writes to the developer's own login keychain or
/// Credential Manager. These facts are the one exception, so they must not fire unless a lane has
/// asked for them.
/// </para>
/// </remarks>
public sealed class OsCredentialStoreRoundTripTests
{

    private const string OptInVariable = "ARCANUM_TEST_OS_CREDENTIAL_STORE";

    private static bool OptedIn() =>
        string.Equals(
            global::System.Environment.GetEnvironmentVariable(OptInVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static void SkipUnlessEnabled(IOsCredentialStore store)
    {

        Skip.IfNot(
            OptedIn(),
            $"Set {OptInVariable}=true to exercise the real OS credential backend; "
            + "this writes to the machine's keychain or Credential Manager.");

        Skip.IfNot(store.IsAvailable, "Requires a usable OS credential backend.");

    }

    // Namespaced per run so a crashed test can never collide with a live entry, and so two CI lanes
    // on the same machine image cannot see each other's secrets.
    private static string UniqueService() =>
        $"arcanum-test-{Guid.NewGuid():N}";

    [SkippableFact]
    public void Set_then_TryGet_returns_the_same_secret()
    {

        OsCredentialStore store = new();

        SkipUnlessEnabled(store);

        string service = UniqueService();

        const string account = "round-trip";

        const string secret = "s3cr3t-value-not-heap-garbage";

        try
        {

            OsCredentialStoreResult written = store.Set(service, account, secret);

            Assert.Equal(OsCredentialStoreStatus.Ok, written.Status);

            OsCredentialStoreResult read = store.TryGet(service, account);

            Assert.Equal(OsCredentialStoreStatus.Ok, read.Status);

            Assert.Equal(secret, read.Value);

        }
        finally
        {

            _ = store.Delete(service, account);

        }

    }

    /// <summary>
    /// A non-ASCII secret is the case a byte-count/char-count mix-up truncates, and the case a
    /// wrong encoding mangles. The Windows store encodes UTF-16 and the POSIX stores UTF-8.
    /// </summary>
    [SkippableFact]
    public void Set_then_TryGet_round_trips_a_non_ascii_secret()
    {

        OsCredentialStore store = new();

        SkipUnlessEnabled(store);

        string service = UniqueService();

        const string account = "round-trip-unicode";

        const string secret = "pässwörd-éè-中文-\U0001F510";

        try
        {

            Assert.Equal(OsCredentialStoreStatus.Ok, store.Set(service, account, secret).Status);

            OsCredentialStoreResult read = store.TryGet(service, account);

            Assert.Equal(OsCredentialStoreStatus.Ok, read.Status);

            Assert.Equal(secret, read.Value);

        }
        finally
        {

            _ = store.Delete(service, account);

        }

    }

    [SkippableFact]
    public void Set_overwrites_an_existing_secret()
    {

        OsCredentialStore store = new();

        SkipUnlessEnabled(store);

        string service = UniqueService();

        const string account = "overwrite";

        try
        {

            Assert.Equal(OsCredentialStoreStatus.Ok, store.Set(service, account, "first").Status);

            Assert.Equal(OsCredentialStoreStatus.Ok, store.Set(service, account, "second").Status);

            OsCredentialStoreResult read = store.TryGet(service, account);

            Assert.Equal(OsCredentialStoreStatus.Ok, read.Status);

            Assert.Equal("second", read.Value);

        }
        finally
        {

            _ = store.Delete(service, account);

        }

    }

    [SkippableFact]
    public void TryGet_reports_NotFound_for_an_unknown_account()
    {

        OsCredentialStore store = new();

        SkipUnlessEnabled(store);

        OsCredentialStoreResult read = store.TryGet(UniqueService(), "never-written");

        Assert.Equal(OsCredentialStoreStatus.NotFound, read.Status);

    }

    [SkippableFact]
    public void Delete_removes_the_secret_and_is_tolerant_of_a_second_call()
    {

        OsCredentialStore store = new();

        SkipUnlessEnabled(store);

        string service = UniqueService();

        const string account = "delete";

        Assert.Equal(OsCredentialStoreStatus.Ok, store.Set(service, account, "transient").Status);

        Assert.Equal(OsCredentialStoreStatus.Ok, store.Delete(service, account).Status);

        Assert.Equal(OsCredentialStoreStatus.NotFound, store.TryGet(service, account).Status);

        // Deleting what is already gone is the desired end state, not an error.
        Assert.Equal(OsCredentialStoreStatus.Ok, store.Delete(service, account).Status);

    }

}
