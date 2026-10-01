using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

/// <summary>
/// The three release routes, driven through the mapped HTTP surface: each lifts the exact fingerprint an
/// erase recorded in its exact scope, never creates the erasure key, and refuses rather than guesses when
/// the evidence cannot be verified.
/// </summary>
/// <remarks>
/// <para>Every fingerprint here comes from an actual erase through the erase routes, and every
/// precondition from a production writer: Saga memories through the store's insert, Lexicon entries
/// through the host's own Lexicon service, Covenant entries through the set routes, and Campaigns through
/// their route. Each test owns one in-memory credential store and hands it to every host it starts, so
/// the erasure key survives a restart exactly as it would in the OS store.</para>
///
/// <para>A successful release goes through the driver, which requires 200; a refusal goes through a
/// raw post and is read by its error code.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class MemoryErasureReleaseEndpointTests
{
    private const string Vault = "Rotate the vault key.";

    private const string Keeper = "Vault Keeper";

    private const string Key = "preference.vault";

    private const string Service = ArcanumCredentialIdentity.Service;

    private const string Account = ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount;

    private static readonly LexiconCurationScope Global = new(LexiconScopeKind.Global, null);

    [SkippableFact]
    public async Task Saga_release_deletes_the_exact_fingerprint_and_the_store_accepts_the_content_again()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        string id = await MemoryErasureRouteDriver.InsertSagaAsync(factory, Vault);

        _ = await driver.EraseSagaAsync(id);

        // The erase is what refuses the content, so the acceptance below is the release's doing.
        Assert.Equal(SagaMemoryWriteOutcome.Suppressed, await MemoryErasureRouteDriver.InsertSagaOutcomeAsync(factory, Vault));

        MemoryErasureReleaseResultDto released = await driver.ReleaseSagaAsync(new(SagaMemoryScopeKind.Global, null, Vault));

        Assert.Equal(new MemoryErasureReleaseResultDto(MemoryReviewStore.Saga, MemoryErasureReleaseOutcome.Released, 1), released);

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        Assert.Equal(SagaMemoryWriteOutcome.Written, await MemoryErasureRouteDriver.InsertSagaOutcomeAsync(factory, Vault));
    }

    /// <summary>
    /// Content read from a file keeps the file's trailing newline, and the release still finds the
    /// fingerprint the erase recorded for the stored text.
    /// </summary>
    [SkippableFact]
    public async Task Saga_release_with_a_trailing_newline_releases_the_trimmed_fingerprint()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        _ = await driver.EraseSagaAsync(await MemoryErasureRouteDriver.InsertSagaAsync(factory, Vault));

        MemoryErasureReleaseResultDto released = await driver.ReleaseSagaAsync(new(SagaMemoryScopeKind.Global, null, Vault + "\n"));

        Assert.Equal(new MemoryErasureReleaseResultDto(MemoryReviewStore.Saga, MemoryErasureReleaseOutcome.Released, 1), released);

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));
    }

    /// <summary>
    /// The exact bytes and their trimmed form are two candidates, and a release deletes whichever were
    /// erased: here both were, as two separate memories.
    /// </summary>
    [SkippableFact]
    public async Task Saga_release_deletes_both_the_exact_and_the_trimmed_fingerprint()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        _ = await driver.EraseSagaAsync(await MemoryErasureRouteDriver.InsertSagaAsync(factory, "Rotate."));

        _ = await driver.EraseSagaAsync(await MemoryErasureRouteDriver.InsertSagaAsync(factory, "Rotate.\n"));

        Assert.Equal(2, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        MemoryErasureReleaseResultDto released = await driver.ReleaseSagaAsync(new(SagaMemoryScopeKind.Global, null, "Rotate.\n"));

        Assert.Equal(new MemoryErasureReleaseResultDto(MemoryReviewStore.Saga, MemoryErasureReleaseOutcome.Released, 2), released);

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));
    }

    /// <summary>
    /// Only trailing whitespace is forgiven. The same word in another Unicode normalization form is a
    /// different identity, so it releases nothing, and the exact form still releases.
    /// </summary>
    [SkippableFact]
    public async Task Saga_release_is_exact_bytes_so_nfd_does_not_release_nfc()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string Nfc = "café";

        const string Nfd = "café";

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        _ = await driver.EraseSagaAsync(await MemoryErasureRouteDriver.InsertSagaAsync(factory, Nfc));

        Assert.Equal(
            new MemoryErasureReleaseResultDto(MemoryReviewStore.Saga, MemoryErasureReleaseOutcome.NotFingerprinted, 0),
            await driver.ReleaseSagaAsync(new(SagaMemoryScopeKind.Global, null, Nfd)));

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        Assert.Equal(
            new MemoryErasureReleaseResultDto(MemoryReviewStore.Saga, MemoryErasureReleaseOutcome.Released, 1),
            await driver.ReleaseSagaAsync(new(SagaMemoryScopeKind.Global, null, Nfc)));

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));
    }

    /// <summary>
    /// An installation that has erased nothing answers without asking the credential store, so a
    /// release can never be the thing that creates the erasure key.
    /// </summary>
    [SkippableTheory]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("covenant")]
    public async Task Release_without_any_evidence_is_NotFingerprinted_and_never_creates_the_key(string store)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        MemoryErasureReleaseResultDto result = store switch
        {
            "saga" => await driver.ReleaseSagaAsync(new(SagaMemoryScopeKind.Global, null, "x")),
            "lexicon" => await driver.ReleaseLexiconAsync(new(Global, "x")),
            _ => await driver.ReleaseCovenantAsync(new(CovenantScope.Global, null, "preference.x")),
        };

        Assert.Equal(new MemoryErasureReleaseResultDto(Store(store), MemoryErasureReleaseOutcome.NotFingerprinted, 0), result);

        Assert.Equal(OsCredentialStoreStatus.NotFound, credentials.ProbePresence(Service, Account));
    }

    [SkippableFact]
    public async Task Release_is_idempotent()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        await ScribeAsync(factory, Keeper, null);

        _ = await driver.EraseLexiconAsync(Keeper, null);

        Assert.Equal(
            new MemoryErasureReleaseResultDto(MemoryReviewStore.Lexicon, MemoryErasureReleaseOutcome.Released, 1),
            await driver.ReleaseLexiconAsync(new(Global, Keeper)));

        Assert.Equal(
            new MemoryErasureReleaseResultDto(MemoryReviewStore.Lexicon, MemoryErasureReleaseOutcome.NotFingerprinted, 0),
            await driver.ReleaseLexiconAsync(new(Global, Keeper)));

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Lexicon));
    }

    /// <summary>
    /// Fingerprints with no key to verify them are lost evidence, not absent evidence: the release says
    /// so, deletes nothing, and does not create a key in their place.
    /// </summary>
    [SkippableFact]
    public async Task Release_with_rows_but_no_key_is_KeyLost_and_deletes_nothing()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using RestartableArcanumProfileFixture profile = new();

        await using (ArcanumWebApplicationFactory first = MemoryErasureRouteDriver.Host(credentials, profile, covenant: true))
        {
            await ScribeAsync(first, Keeper, null);

            _ = await new MemoryErasureRouteDriver(first.CreateClient()).EraseLexiconAsync(Keeper, null);
        }

        Assert.Equal(OsCredentialStoreStatus.Ok, credentials.Delete(Service, Account).Status);

        await using ArcanumWebApplicationFactory second = MemoryErasureRouteDriver.Host(credentials, profile, covenant: true);

        await AssertRefusedAsync(
            second,
            "/api/memory/lexicon/release",
            JsonBody(new LexiconErasureReleaseRequest(Global, Keeper)),
            HttpStatusCode.Conflict,
            ErrorCodes.MemoryErasure.KeyLost);

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(second, MemoryReviewStore.Lexicon));

        Assert.Equal(OsCredentialStoreStatus.NotFound, credentials.ProbePresence(Service, Account));
    }

    /// <summary>
    /// A key that replaced the one the fingerprints were recorded under can match none of them, so the
    /// release finds nothing to delete and reports the evidence as unverifiable rather than absent.
    /// </summary>
    [SkippableFact]
    public async Task Release_under_a_replaced_key_is_KeyLost_and_deletes_nothing()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using RestartableArcanumProfileFixture profile = new();

        await using (ArcanumWebApplicationFactory first = MemoryErasureRouteDriver.Host(credentials, profile, covenant: true))
        {
            await ScribeAsync(first, Keeper, null);

            _ = await new MemoryErasureRouteDriver(first.CreateClient()).EraseLexiconAsync(Keeper, null);
        }

        string replacement = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        Assert.Equal(OsCredentialStoreStatus.Ok, credentials.Set(Service, Account, replacement).Status);

        await using ArcanumWebApplicationFactory second = MemoryErasureRouteDriver.Host(credentials, profile, covenant: true);

        await AssertRefusedAsync(
            second,
            "/api/memory/lexicon/release",
            JsonBody(new LexiconErasureReleaseRequest(Global, Keeper)),
            HttpStatusCode.Conflict,
            ErrorCodes.MemoryErasure.KeyLost);

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(second, MemoryReviewStore.Lexicon));

        Assert.Equal(replacement, credentials.TryGet(Service, Account).Value);
    }

    /// <summary>
    /// A Lexicon name is released the way the scribe chokepoint reads it, trimmed and case-folded, but
    /// only in the exact scope it was erased in.
    /// </summary>
    [SkippableFact]
    public async Task Lexicon_release_normalizes_the_name_and_honors_the_exact_scope()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        Guid erasedIn = await RegisterCampaignAsync(factory, "c");

        Guid other = await RegisterCampaignAsync(factory, "d");

        await ScribeAsync(factory, Keeper, erasedIn);

        _ = await driver.EraseLexiconAsync(Keeper, erasedIn);

        Assert.Equal(
            new MemoryErasureReleaseResultDto(MemoryReviewStore.Lexicon, MemoryErasureReleaseOutcome.NotFingerprinted, 0),
            await driver.ReleaseLexiconAsync(new(Global, "  vault keeper ")));

        Assert.Equal(
            new MemoryErasureReleaseResultDto(MemoryReviewStore.Lexicon, MemoryErasureReleaseOutcome.NotFingerprinted, 0),
            await driver.ReleaseLexiconAsync(new(new LexiconCurationScope(LexiconScopeKind.Campaign, other), "vault keeper")));

        Assert.Equal(
            new MemoryErasureReleaseResultDto(MemoryReviewStore.Lexicon, MemoryErasureReleaseOutcome.Released, 1),
            await driver.ReleaseLexiconAsync(new(new LexiconCurationScope(LexiconScopeKind.Campaign, erasedIn), "  vault keeper ")));

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Lexicon));
    }

    /// <summary>
    /// The Covenant key grammar is lower-case only and nothing is folded, so a key spelled any other way
    /// is refused as malformed rather than released, and a well-formed key releases only in its scope.
    /// </summary>
    [SkippableFact]
    public async Task Covenant_release_refuses_a_malformed_key_and_honors_the_exact_scope()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        Guid erasedIn = await RegisterCampaignAsync(factory, "c");

        Guid other = await RegisterCampaignAsync(factory, "d");

        _ = await driver.SetCovenantAsync(CovenantScope.Campaign, erasedIn, Key, "Keep the vault key offline.");

        _ = await driver.EraseCovenantAsync(CovenantScope.Campaign, erasedIn, Key);

        await AssertRefusedAsync(
            factory,
            "/api/memory/covenant/release",
            JsonBody(new CovenantErasureReleaseRequest(CovenantScope.Campaign, erasedIn, "Preference.Vault")),
            HttpStatusCode.BadRequest,
            ErrorCodes.Covenant.InvalidKey);

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Covenant));

        Assert.Equal(
            new MemoryErasureReleaseResultDto(MemoryReviewStore.Covenant, MemoryErasureReleaseOutcome.NotFingerprinted, 0),
            await driver.ReleaseCovenantAsync(new(CovenantScope.Global, null, Key)));

        Assert.Equal(
            new MemoryErasureReleaseResultDto(MemoryReviewStore.Covenant, MemoryErasureReleaseOutcome.NotFingerprinted, 0),
            await driver.ReleaseCovenantAsync(new(CovenantScope.Campaign, other, Key)));

        Assert.Equal(
            new MemoryErasureReleaseResultDto(MemoryReviewStore.Covenant, MemoryErasureReleaseOutcome.Released, 1),
            await driver.ReleaseCovenantAsync(new(CovenantScope.Campaign, erasedIn, Key)));

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Covenant));
    }

    [SkippableTheory]
    [InlineData("saga", """{"scopeKind":2,"campaignId":null,"content":"x"}""", ErrorCodes.Validation.InvalidBody)]
    [InlineData("saga", """{"scopeKind":1,"campaignId":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","content":"x"}""", ErrorCodes.Validation.InvalidBody)]
    [InlineData("saga", """{"scopeKind":1,"campaignId":null,"content":""}""", ErrorCodes.Validation.InvalidBody)]
    [InlineData("lexicon", """{"scope":{"kind":"Campaign","campaignId":null},"name":"x"}""", ErrorCodes.Lexicon.InvalidScope)]
    [InlineData("lexicon", """{"scope":{"kind":"Global","campaignId":null},"name":"   "}""", ErrorCodes.Lexicon.InvalidName)]
    [InlineData("covenant", """{"scope":"Global","campaignId":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","key":"a"}""", ErrorCodes.Covenant.InvalidScope)]
    public async Task Release_requests_that_fail_validation_answer_400_and_change_nothing(string store, string body, string code)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        long before = await MemoryErasureRouteDriver.FingerprintCountAsync(factory, Store(store));

        await AssertRefusedAsync(factory, PathOf(store), RawBody(body), HttpStatusCode.BadRequest, code);

        Assert.Equal(before, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, Store(store)));

        Assert.Equal(OsCredentialStoreStatus.NotFound, credentials.ProbePresence(Service, Account));
    }

    [SkippableFact]
    public async Task Release_responses_carry_the_private_no_store_tuple()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        HttpClient client = factory.CreateAuthenticatedClient();

        using (HttpResponseMessage released = await client.PostAsync(
            "/api/memory/lexicon/release",
            JsonBody(new LexiconErasureReleaseRequest(Global, Keeper))))
        {
            Assert.Equal(HttpStatusCode.OK, released.StatusCode);

            AssertProtectedTuple(released);
        }

        foreach (string store in (string[])["saga", "lexicon", "covenant"])
        {
            using HttpResponseMessage refused = await client.PostAsync(PathOf(store), RawBody("{}"));

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

            Assert.Equal(ErrorCodes.Validation.InvalidBody, await MemoryErasureRouteDriver.ReadErrorCodeAsync(refused));

            AssertProtectedTuple(refused);
        }
    }

    private static MemoryReviewStore Store(string store) => store switch
    {
        "saga" => MemoryReviewStore.Saga,
        "lexicon" => MemoryReviewStore.Lexicon,
        "covenant" => MemoryReviewStore.Covenant,
        _ => throw new ArgumentOutOfRangeException(nameof(store), store, "A recognized store is required."),
    };

    private static string PathOf(string store) => $"/api/memory/{store}/release";

    /// <summary>
    /// Posts one raw body with the test API key and requires the refusal it names, with the protected
    /// tuple.
    /// </summary>
    private static async Task AssertRefusedAsync(
        ArcanumWebApplicationFactory factory,
        string path,
        StringContent body,
        HttpStatusCode status,
        string code)
    {
        using HttpResponseMessage refused = await factory.CreateAuthenticatedClient().PostAsync(path, body);

        Assert.Equal(status, refused.StatusCode);

        Assert.Equal(code, await MemoryErasureRouteDriver.ReadErrorCodeAsync(refused));

        AssertProtectedTuple(refused);
    }

    private static StringContent JsonBody(LexiconErasureReleaseRequest request) =>
        RawBody(System.Text.Json.JsonSerializer.Serialize(request, ArcanumJsonContext.Default.LexiconErasureReleaseRequest));

    private static StringContent JsonBody(CovenantErasureReleaseRequest request) =>
        RawBody(System.Text.Json.JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CovenantErasureReleaseRequest));

    private static StringContent RawBody(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>Scribes one entry through the host's own Lexicon service, the one every writer holds.</summary>
    private static async Task ScribeAsync(ArcanumWebApplicationFactory factory, string name, Guid? campaignId)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        Result<LexiconEntryDto> scribed = await scope.ServiceProvider
            .GetRequiredService<ILexiconService>()
            .UpsertAsync(name, "Person", ["keeps the vault key"], LexiconScope.ForResolvedCampaign(campaignId), CancellationToken.None);

        Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : null);
    }

    /// <summary>Registers one Campaign through its route, so the Covenant gate knows the scope it leases.</summary>
    private static async Task<Guid> RegisterCampaignAsync(ArcanumWebApplicationFactory factory, string suffix)
    {
        string path = Path.Combine(factory.TempHome, $"release-campaign-{suffix}");

        Directory.CreateDirectory(path);

        using HttpResponseMessage registered = await factory.CreateAuthenticatedClient().PostAsync(
            "/api/campaigns",
            JsonContent.Create(
                new RegisterCampaignRequest($"Release {suffix}", path, WorkspaceType.Campaign, null),
                ArcanumJsonContext.Default.RegisterCampaignRequest));

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        return (await MemoryErasureRouteDriver.ReadDataAsync(registered, ArcanumJsonContext.Default.ApiResponseCampaignDto)).Id;
    }

    /// <summary>The tuple every release response carries whatever its status (API §8.29, §8.35).</summary>
    private static void AssertProtectedTuple(HttpResponseMessage response)
    {
        Assert.Equal("no-store, private", response.Headers.CacheControl!.ToString());

        Assert.Equal("no-cache", Assert.Single(response.Headers.GetValues("Pragma")));

        Assert.Equal("0", Assert.Single(response.Content.Headers.GetValues("Expires")));

        Assert.Null(response.Headers.ETag);

        Assert.False(response.Content.Headers.Contains("Last-Modified"));
    }
}
