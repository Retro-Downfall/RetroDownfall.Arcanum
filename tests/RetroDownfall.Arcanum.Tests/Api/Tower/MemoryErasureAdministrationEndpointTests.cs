using System.Buffers.Text;
using System.Data;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

/// <summary>
/// The erasure status, scrub and key-reset routes, driven through the mapped HTTP surface over evidence
/// that only actual erases recorded.
/// </summary>
/// <remarks>
/// <para>Every test owns one in-memory credential store and one recorder in front of it, and hands both to
/// every host it starts, so the erasure key survives a restart exactly as it would in the OS store and
/// every secret read, write and presence probe any host made is counted. Losing or replacing the key is
/// done to that store between hosts, the way an operator or a second test home would do it.</para>
///
/// <para>Counts that the setup itself moves, such as the first erase's key creation, are read as deltas
/// from the point the setup finished, so a refusal is shown to have written nothing of its own.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class MemoryErasureAdministrationEndpointTests
{
    private const string Service = ArcanumCredentialIdentity.Service;

    private const string Account = ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount;

    private const string StatusPath = "/api/memory/erasure";

    private const string ScrubPath = "/api/memory/erasure/scrub";

    private const string PreparePath = "/api/memory/erasure/reset-key/prepare";

    private const string ResetPath = "/api/memory/erasure/reset-key";

    private const string Keeper = "Vault Keeper";

    private const string Vault = "Rotate the vault key.";

    private const string Harbor = "The harbor chain is raised at dusk.";

    private static readonly MemoryReviewStore[] StoreOrder =
        [MemoryReviewStore.Covenant, MemoryReviewStore.Saga, MemoryReviewStore.Lexicon];

    [SkippableFact]
    public async Task Status_on_a_fresh_installation_is_Absent_with_zero_counts_and_reads_no_secret()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials);

        MemoryErasureStatusDto status = await StatusAsync(factory);

        Assert.Equal(MemoryErasureKeyStatus.Absent, status.KeyStatus);

        Assert.Equal(StoreOrder, status.Stores.Select(static store => store.Store));

        Assert.All(status.Stores, static store => Assert.Equal((0L, 0L, 0L), (store.Fingerprints, store.Unverifiable, store.Receipts)));

        Assert.Equal(0, status.PendingScrubReceipts);

        Assert.Equal(0, credentials.TryGetCount(Account));

        Assert.Equal(1, credentials.ProbeCount(Account));
    }

    /// <summary>
    /// One erase is one fingerprint and one receipt however many twins it removed; the receipt names each
    /// erased row as a subject.
    /// </summary>
    [SkippableFact]
    public async Task Status_counts_one_fingerprint_and_one_receipt_per_erase_even_with_twins()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        string first = await MemoryErasureRouteDriver.InsertSagaAsync(factory, Vault);

        _ = await MemoryErasureRouteDriver.InsertSagaAsync(factory, Vault);

        MemoryErasureRoundTrip<SagaEraseRequest> erased = await driver.EraseSagaAsync(first);

        Assert.Equal(2, erased.Result.Local.ErasedItemCount);

        await ScribeAsync(factory, Keeper);

        _ = await driver.EraseLexiconAsync(Keeper, null);

        MemoryErasureStatusDto status = await StatusAsync(factory);

        Assert.Equal(MemoryErasureKeyStatus.Present, status.KeyStatus);

        AssertStores(status.Stores, covenant: (0, 0, 0), saga: (1, 0, 1), lexicon: (1, 0, 1));

        Assert.Equal(0, status.PendingScrubReceipts);

        Assert.Equal(3, await ScalarAsync(factory, "SELECT count(*) FROM memory_erasure_receipt_subjects;"));
    }

    /// <summary>
    /// A key the credential store cannot read says nothing about which rows it would verify, so status
    /// reports every row's existence and reads the unverifiable counts as zero, meaning unknown.
    /// </summary>
    [SkippableFact]
    public async Task Status_reports_unverifiable_as_unknown_while_the_key_is_unavailable()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        await using RestartableArcanumProfileFixture profile = new();

        await LostKeySetupAsync(inner, credentials, profile);

        credentials.FailAccount(Account, OsCredentialStoreStatus.Unavailable);

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials, profile);

        MemoryErasureStatusDto status = await StatusAsync(factory);

        Assert.Equal(MemoryErasureKeyStatus.Unavailable, status.KeyStatus);

        AssertStores(status.Stores, covenant: (0, 0, 0), saga: (1, 0, 1), lexicon: (1, 0, 1));
    }

    /// <summary>
    /// With no evidence, status asks only whether the item exists, which cannot tell a malformed item
    /// from a key. Once a read has found the item malformed, status says so too, still without reading
    /// the secret itself.
    /// </summary>
    [SkippableFact]
    public async Task Status_without_evidence_reports_a_malformed_item_once_a_read_has_found_it()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        Assert.Equal(OsCredentialStoreStatus.Ok, credentials.Set(Service, Account, "not base64url").Status);

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials);

        Assert.Equal(MemoryErasureKeyStatus.Present, (await StatusAsync(factory)).KeyStatus);

        Assert.Equal(0, credentials.TryGetCount(Account));

        string memory = await MemoryErasureRouteDriver.InsertSagaAsync(factory, Vault);

        using (HttpResponseMessage refused = await PrepareSagaEraseAsync(factory, memory))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

            Assert.Equal(ErrorCodes.MemoryErasure.KeyUnavailable, await MemoryErasureRouteDriver.ReadErrorCodeAsync(refused));
        }

        int reads = credentials.TryGetCount(Account);

        MemoryErasureStatusDto status = await StatusAsync(factory);

        Assert.Equal(MemoryErasureKeyStatus.Unavailable, status.KeyStatus);

        AssertStores(status.Stores, covenant: (0, 0, 0), saga: (0, 0, 0), lexicon: (0, 0, 0));

        Assert.Equal(reads, credentials.TryGetCount(Account));

        Assert.Equal("not base64url", inner.TryGet(Service, Account).Value);
    }

    /// <summary>An item that is not a key, with rows to verify, reads as unavailable and its rows as unknown.</summary>
    [SkippableFact]
    public async Task Status_reports_a_malformed_item_with_rows_as_unavailable()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        await using RestartableArcanumProfileFixture profile = new();

        await using (ArcanumWebApplicationFactory first = Host(inner, credentials, profile))
        {
            await ScribeAsync(first, Keeper);

            _ = await new MemoryErasureRouteDriver(first.CreateClient()).EraseLexiconAsync(Keeper, null);
        }

        Assert.Equal(OsCredentialStoreStatus.Ok, credentials.Set(Service, Account, "not base64url").Status);

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials, profile);

        MemoryErasureStatusDto status = await StatusAsync(factory);

        Assert.Equal(MemoryErasureKeyStatus.Unavailable, status.KeyStatus);

        AssertStores(status.Stores, covenant: (0, 0, 0), saga: (0, 0, 0), lexicon: (1, 0, 1));
    }

    /// <summary>A store that cannot answer even whether the item exists reads as unavailable.</summary>
    [SkippableFact]
    public async Task Status_without_evidence_reports_an_unreadable_store_as_unavailable()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        credentials.FailAccount(Account, OsCredentialStoreStatus.Unavailable);

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials);

        MemoryErasureStatusDto status = await StatusAsync(factory);

        Assert.Equal(MemoryErasureKeyStatus.Unavailable, status.KeyStatus);

        AssertStores(status.Stores, covenant: (0, 0, 0), saga: (0, 0, 0), lexicon: (0, 0, 0));

        Assert.Equal(0, credentials.TryGetCount(Account));

        Assert.Equal(1, credentials.ProbeCount(Account));
    }

    [SkippableFact]
    public async Task Scrub_with_nothing_pending_reports_NotAttempted()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        MemoryErasureRoundTrip<SagaEraseRequest> erased = await driver.EraseSagaAsync(
            await MemoryErasureRouteDriver.InsertSagaAsync(factory, Vault));

        // A quiet host truncates the log right after the commit, so nothing is left for the scrub.
        Assert.Equal(MemoryLocalErasureOutcome.Verified, erased.Result.Local.Outcome);

        using HttpResponseMessage scrubbed = await factory.CreateAuthenticatedClient().PostAsync(ScrubPath, content: null);

        Assert.Equal(HttpStatusCode.OK, scrubbed.StatusCode);

        AssertProtectedTuple(scrubbed);

        Assert.Equal(
            new MemoryErasureScrubResultDto(MemoryErasureWalCheckpointAttempt.NotAttempted, 0, 0),
            await MemoryErasureRouteDriver.ReadDataAsync(scrubbed, ArcanumJsonContext.Default.ApiResponseMemoryErasureScrubResultDto));
    }

    /// <summary>
    /// The scrub finishes what an erase left pending on the log, through the routes and across a
    /// restart: busy while a reader holds the log, then verified once the reader is gone.
    /// </summary>
    [SkippableFact]
    public async Task Scrub_after_a_restart_verifies_a_receipt_a_held_reader_left_pending()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        await using RestartableArcanumProfileFixture profile = new();

        MemoryErasureRoundTrip<SagaEraseRequest> erased;

        await using (ArcanumWebApplicationFactory first = Host(inner, credentials, profile))
        {
            MemoryErasureRouteDriver driver = new(first.CreateClient());

            string memory = await MemoryErasureRouteDriver.InsertSagaAsync(first, Vault);

            await using (await MemoryErasureRouteDriver.HoldReaderAsync(first))
            {
                erased = await driver.EraseSagaAsync(memory);

                Assert.Equal(
                    new MemoryErasureScrubResultDto(MemoryErasureWalCheckpointAttempt.Busy, 0, 1),
                    await ScrubAsync(first));
            }

            Assert.Equal(MemoryLocalErasureOutcome.RowsRemovedScrubPending, erased.Result.Local.Outcome);

            Assert.Equal<MemoryErasureScrubPendingReason>([MemoryErasureScrubPendingReason.WalCheckpointPending], erased.Result.Local.PendingReasons);

            Assert.Equal(MemoryErasureWalCheckpointAttempt.Busy, erased.Result.Local.WalCheckpointAttempt);
        }

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials, profile);

        Assert.Equal(1, (await StatusAsync(factory)).PendingScrubReceipts);

        Assert.Equal(
            new MemoryErasureScrubResultDto(MemoryErasureWalCheckpointAttempt.Truncated, 1, 0),
            await ScrubAsync(factory));

        Assert.Equal(0, (await StatusAsync(factory)).PendingScrubReceipts);

        Assert.Equal(
            2,
            await ScalarAsync(
                factory,
                $"SELECT ScrubStateCode FROM memory_erasure_receipts WHERE MutationId = '{erased.Result.MutationId.ToString("D").ToUpperInvariant()}' AND ScrubPendingReasonMask = 0;"));

        MemoryErasureResultDto replayed = await new MemoryErasureRouteDriver(factory.CreateClient()).ApplySagaAsync(erased.Apply);

        Assert.True(replayed.Replayed);

        Assert.Equal(MemoryLocalErasureOutcome.Verified, replayed.Local.Outcome);
    }

    /// <summary>
    /// A reset whose counts went stale while the key was lost creates the key before its transaction
    /// finds the counts changed (spec §5.7). The refusal discards nothing and the new key stays: the
    /// rows now read as unverifiable under it, the writers stay refused, and a fresh reset finishes.
    /// </summary>
    [SkippableFact]
    public async Task A_count_stale_reset_on_a_lost_key_creates_the_key_refuses_and_a_fresh_reset_recovers()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        await using RestartableArcanumProfileFixture profile = new();

        await LostKeySetupAsync(inner, credentials, profile);

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials, profile);

        string stale = factory.Services
            .GetRequiredService<IMemoryErasureTokenCodec>()
            .IssueErasureKeyReset(new(MemoryErasureKeyStatus.Lost, 0, 5, 5, 0, 5, 5))
            .Value
            .Token;

        await AssertRefusedAsync(factory, ResetPath, ResetBody(stale), HttpStatusCode.Conflict, ErrorCodes.MemoryErasure.StalePlan);

        Assert.Equal(OsCredentialStoreStatus.Ok, inner.ProbePresence(Service, Account));

        MemoryErasureStatusDto replaced = await StatusAsync(factory);

        Assert.Equal(MemoryErasureKeyStatus.Present, replaced.KeyStatus);

        AssertStores(replaced.Stores, covenant: (0, 0, 0), saga: (1, 1, 1), lexicon: (1, 1, 1));

        Result<LexiconEntryDto> scribed = await ScribeResultAsync(factory, "Harbor Master");

        Assert.True(scribed.IsFailure);

        Assert.Equal(ErrorCodes.MemoryErasure.KeyLost, scribed.Error.Code);

        MemoryErasureKeyResetPreflightDto prepared = await PrepareAsync(factory);

        Assert.Equal(MemoryErasureKeyStatus.Present, prepared.KeyStatus);

        AssertStores(prepared.Stores, covenant: (0, 0, 0), saga: (1, 1, 1), lexicon: (1, 1, 1));

        Assert.Equal(
            new MemoryErasureKeyResetResultDto(MemoryErasureKeyStatus.Present, 2, 2, KeyCreated: false),
            await ResetAsync(factory, prepared.PreflightToken));

        AssertStores((await StatusAsync(factory)).Stores, covenant: (0, 0, 0), saga: (0, 0, 0), lexicon: (0, 0, 0));
    }

    /// <summary>
    /// A second test home overwrote the shared account, so this installation holds rows two keys
    /// recorded. The reset discards only what the present key cannot verify and keeps that key.
    /// </summary>
    [SkippableFact]
    public async Task Reset_key_discards_only_rows_under_a_foreign_key_and_keeps_the_present_key()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        await using RestartableArcanumProfileFixture profile = new();

        string replacement = await ForeignKeySetupAsync(inner, credentials, profile);

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials, profile);

        int writes = credentials.SetCount(Account);

        MemoryErasureKeyResetPreflightDto prepared = await PrepareAsync(factory);

        Assert.Equal(MemoryErasureKeyStatus.Present, prepared.KeyStatus);

        AssertStores(prepared.Stores, covenant: (0, 0, 0), saga: (1, 0, 1), lexicon: (1, 1, 1));

        MemoryErasureKeyResetResultDto reset = await ResetAsync(factory, prepared.PreflightToken);

        Assert.Equal(new MemoryErasureKeyResetResultDto(MemoryErasureKeyStatus.Present, 1, 1, KeyCreated: false), reset);

        MemoryErasureStatusDto status = await StatusAsync(factory);

        Assert.Equal(MemoryErasureKeyStatus.Present, status.KeyStatus);

        AssertStores(status.Stores, covenant: (0, 0, 0), saga: (1, 0, 1), lexicon: (0, 0, 0));

        Assert.Equal(replacement, inner.TryGet(Service, Account).Value);

        Assert.Equal(writes, credentials.SetCount(Account));

        // The discarded Lexicon erasure no longer refuses its name, and the kept Saga one still does.
        await ScribeAsync(factory, Keeper);

        Assert.Equal(SagaMemoryWriteOutcome.Suppressed, await MemoryErasureRouteDriver.InsertSagaOutcomeAsync(factory, Harbor));
    }

    /// <summary>The key was deleted while rows exist: the reset creates a key and discards every row.</summary>
    [SkippableFact]
    public async Task Reset_key_creates_the_key_when_it_is_NotFound_and_rows_exist()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        await using RestartableArcanumProfileFixture profile = new();

        await LostKeySetupAsync(inner, credentials, profile);

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials, profile);

        MemoryErasureKeyResetPreflightDto prepared = await PrepareAsync(factory);

        Assert.Equal(MemoryErasureKeyStatus.Lost, prepared.KeyStatus);

        AssertStores(prepared.Stores, covenant: (0, 0, 0), saga: (1, 1, 1), lexicon: (1, 1, 1));

        Assert.True(prepared.ExpiresAtUtc > prepared.IssuedAtUtc);

        Assert.Equal(OsCredentialStoreStatus.NotFound, inner.ProbePresence(Service, Account));

        MemoryErasureKeyResetResultDto reset = await ResetAsync(factory, prepared.PreflightToken);

        Assert.Equal(new MemoryErasureKeyResetResultDto(MemoryErasureKeyStatus.Present, 2, 2, KeyCreated: true), reset);

        Assert.Equal(OsCredentialStoreStatus.Ok, inner.ProbePresence(Service, Account));

        MemoryErasureStatusDto status = await StatusAsync(factory);

        Assert.Equal(MemoryErasureKeyStatus.Present, status.KeyStatus);

        AssertStores(status.Stores, covenant: (0, 0, 0), saga: (0, 0, 0), lexicon: (0, 0, 0));

        Assert.Equal(0, status.PendingScrubReceipts);

        Assert.Equal(0, await ScalarAsync(factory, "SELECT count(*) FROM memory_erasure_receipt_subjects;"));
    }

    /// <summary>A reset that already ran finds nothing to discard, so applying its token again succeeds and changes nothing.</summary>
    [SkippableFact]
    public async Task Reset_key_is_idempotent()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        await using RestartableArcanumProfileFixture profile = new();

        await LostKeySetupAsync(inner, credentials, profile);

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials, profile);

        MemoryErasureKeyResetPreflightDto prepared = await PrepareAsync(factory);

        Assert.Equal(
            new MemoryErasureKeyResetResultDto(MemoryErasureKeyStatus.Present, 2, 2, KeyCreated: true),
            await ResetAsync(factory, prepared.PreflightToken));

        string key = inner.TryGet(Service, Account).Value!;

        int writes = credentials.SetCount(Account);

        Assert.Equal(
            new MemoryErasureKeyResetResultDto(MemoryErasureKeyStatus.Present, 0, 0, KeyCreated: false),
            await ResetAsync(factory, prepared.PreflightToken));

        Assert.Equal(key, inner.TryGet(Service, Account).Value);

        Assert.Equal(writes, credentials.SetCount(Account));

        MemoryErasureKeyResetPreflightDto again = await PrepareAsync(factory);

        Assert.Equal(MemoryErasureKeyStatus.Present, again.KeyStatus);

        Assert.All(again.Stores, static store => Assert.Equal(0, store.Unverifiable));
    }

    /// <summary>
    /// A key the credential store cannot read refuses the reset before anything changes: no key is
    /// written and no row is discarded.
    /// </summary>
    [SkippableFact]
    public async Task Reset_key_refuses_an_unavailable_key_and_changes_nothing()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        await using RestartableArcanumProfileFixture profile = new();

        await LostKeySetupAsync(inner, credentials, profile);

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials, profile);

        int writes = credentials.SetCount(Account);

        MemoryErasureKeyResetPreflightDto prepared = await PrepareAsync(factory);

        Assert.Equal(MemoryErasureKeyStatus.Lost, prepared.KeyStatus);

        credentials.FailAccount(Account, OsCredentialStoreStatus.Unavailable);

        await AssertRefusedAsync(
            factory,
            ResetPath,
            ResetBody(prepared.PreflightToken),
            HttpStatusCode.ServiceUnavailable,
            ErrorCodes.MemoryErasure.KeyUnavailable);

        Assert.Equal(writes, credentials.SetCount(Account));

        credentials.ClearFailure(Account);

        Assert.Equal(OsCredentialStoreStatus.NotFound, inner.ProbePresence(Service, Account));

        MemoryErasureStatusDto status = await StatusAsync(factory);

        Assert.Equal(MemoryErasureKeyStatus.Lost, status.KeyStatus);

        AssertStores(status.Stores, covenant: (0, 0, 0), saga: (1, 1, 1), lexicon: (1, 1, 1));
    }

    /// <summary>
    /// A stored item that is not a key is never overwritten: prepare and apply both refuse, and the
    /// operator removes the item with the OS credential tool.
    /// </summary>
    [SkippableFact]
    public async Task Reset_key_refuses_a_malformed_item_without_overwriting_it()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        await using RestartableArcanumProfileFixture profile = new();

        await using (ArcanumWebApplicationFactory first = Host(inner, credentials, profile))
        {
            await ScribeAsync(first, Keeper);

            _ = await new MemoryErasureRouteDriver(first.CreateClient()).EraseLexiconAsync(Keeper, null);
        }

        int writes = credentials.SetCount(Account);

        Assert.Equal(OsCredentialStoreStatus.Ok, credentials.Set(Service, Account, "not base64url").Status);

        Assert.Equal(writes + 1, credentials.SetCount(Account));

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials, profile);

        await AssertRefusedAsync(factory, PreparePath, content: null, HttpStatusCode.ServiceUnavailable, ErrorCodes.MemoryErasure.KeyUnavailable);

        string forged = factory.Services
            .GetRequiredService<IMemoryErasureTokenCodec>()
            .IssueErasureKeyReset(new(MemoryErasureKeyStatus.Lost, 0, 0, 1, 0, 0, 1))
            .Value
            .Token;

        await AssertRefusedAsync(factory, ResetPath, ResetBody(forged), HttpStatusCode.ServiceUnavailable, ErrorCodes.MemoryErasure.KeyUnavailable);

        Assert.Equal("not base64url", inner.TryGet(Service, Account).Value);

        Assert.Equal(writes + 1, credentials.SetCount(Account));

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Lexicon));

        Assert.Equal(1, await ScalarAsync(factory, "SELECT count(*) FROM memory_erasure_receipts;"));
    }

    [SkippableFact]
    public async Task Reset_key_apply_with_counts_that_no_longer_match_is_StalePlan()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        await using RestartableArcanumProfileFixture profile = new();

        _ = await ForeignKeySetupAsync(inner, credentials, profile);

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials, profile);

        string stale = factory.Services
            .GetRequiredService<IMemoryErasureTokenCodec>()
            .IssueErasureKeyReset(new(MemoryErasureKeyStatus.Present, 0, 0, 5, 0, 0, 5))
            .Value
            .Token;

        await AssertRefusedAsync(factory, ResetPath, ResetBody(stale), HttpStatusCode.Conflict, ErrorCodes.MemoryErasure.StalePlan);

        AssertStores((await StatusAsync(factory)).Stores, covenant: (0, 0, 0), saga: (1, 0, 1), lexicon: (1, 1, 1));
    }

    /// <summary>
    /// The totals a preview showed can survive while the rows behind them move between stores. Here a
    /// second home's key is swapped back in after prepare: the Saga row it could not verify becomes
    /// verifiable, and the Lexicon row it recorded becomes unverifiable. One fingerprint and one receipt
    /// are unverifiable before and after, in different stores, so the reset discards nothing.
    /// </summary>
    [SkippableFact]
    public async Task Reset_key_apply_after_a_same_total_shift_between_stores_is_StalePlan()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        await using RestartableArcanumProfileFixture profile = new();

        await using (ArcanumWebApplicationFactory first = Host(inner, credentials, profile))
        {
            _ = await new MemoryErasureRouteDriver(first.CreateClient()).EraseSagaAsync(
                await MemoryErasureRouteDriver.InsertSagaAsync(first, Harbor));
        }

        string original = inner.TryGet(Service, Account).Value!;

        Assert.Equal(OsCredentialStoreStatus.Ok, credentials.Set(Service, Account, NewKey()).Status);

        MemoryErasureKeyResetPreflightDto prepared;

        await using (ArcanumWebApplicationFactory second = Host(inner, credentials, profile))
        {
            await ScribeAsync(second, Keeper);

            _ = await new MemoryErasureRouteDriver(second.CreateClient()).EraseLexiconAsync(Keeper, null);

            prepared = await PrepareAsync(second);
        }

        Assert.Equal(MemoryErasureKeyStatus.Present, prepared.KeyStatus);

        AssertStores(prepared.Stores, covenant: (0, 0, 0), saga: (1, 1, 1), lexicon: (1, 0, 1));

        Assert.Equal(OsCredentialStoreStatus.Ok, credentials.Set(Service, Account, original).Status);

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials, profile);

        AssertStores((await StatusAsync(factory)).Stores, covenant: (0, 0, 0), saga: (1, 0, 1), lexicon: (1, 1, 1));

        await AssertRefusedAsync(factory, ResetPath, ResetBody(prepared.PreflightToken), HttpStatusCode.Conflict, ErrorCodes.MemoryErasure.StalePlan);

        AssertStores((await StatusAsync(factory)).Stores, covenant: (0, 0, 0), saga: (1, 0, 1), lexicon: (1, 1, 1));

        Assert.Equal(original, inner.TryGet(Service, Account).Value);
    }

    /// <summary>
    /// A token prepared while a key was present cannot authorize creating a new one after that key is
    /// gone: the reset refuses before any keychain write.
    /// </summary>
    [SkippableFact]
    public async Task Reset_key_whose_key_state_no_longer_holds_is_StalePlan()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        await using RestartableArcanumProfileFixture profile = new();

        _ = await ForeignKeySetupAsync(inner, credentials, profile);

        MemoryErasureKeyResetPreflightDto prepared;

        await using (ArcanumWebApplicationFactory second = Host(inner, credentials, profile))
        {
            prepared = await PrepareAsync(second);
        }

        Assert.Equal(MemoryErasureKeyStatus.Present, prepared.KeyStatus);

        Assert.Equal(OsCredentialStoreStatus.Ok, credentials.Delete(Service, Account).Status);

        int writes = credentials.SetCount(Account);

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials, profile);

        await AssertRefusedAsync(factory, ResetPath, ResetBody(prepared.PreflightToken), HttpStatusCode.Conflict, ErrorCodes.MemoryErasure.StalePlan);

        Assert.Equal(OsCredentialStoreStatus.NotFound, inner.ProbePresence(Service, Account));

        Assert.Equal(writes, credentials.SetCount(Account));

        AssertStores((await StatusAsync(factory)).Stores, covenant: (0, 0, 0), saga: (1, 1, 1), lexicon: (1, 1, 1));
    }

    [SkippableFact]
    public async Task Reset_key_with_an_undecodable_token_is_InvalidPreflight()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials);

        await AssertRefusedAsync(
            factory,
            ResetPath,
            new StringContent("""{"preflightToken":"x"}""", Encoding.UTF8, "application/json"),
            HttpStatusCode.BadRequest,
            ErrorCodes.MemoryErasure.InvalidPreflight);

        Assert.Equal(0, credentials.SetCount(Account));
    }

    private static ArcanumWebApplicationFactory Host(
        InMemoryOsCredentialStore inner,
        SecretAccessRecordingCredentialStore credentials,
        RestartableArcanumProfileFixture? profile = null) =>
        MemoryErasureRouteDriver.Host(inner, profile, covenant: true).WithRecordingCredentials(credentials);

    /// <summary>
    /// Host 1 erases the Lexicon entry under its key; a second home then overwrites the shared account,
    /// and host 2 erases one Saga memory under the replacement. The Lexicon store holds a row the
    /// replacement cannot verify, so its own erase there would be refused as lost; the Saga store holds
    /// only the replacement's row.
    /// </summary>
    /// <returns>The replacement key, as stored.</returns>
    private static async Task<string> ForeignKeySetupAsync(
        InMemoryOsCredentialStore inner,
        SecretAccessRecordingCredentialStore credentials,
        RestartableArcanumProfileFixture profile)
    {
        await using (ArcanumWebApplicationFactory first = Host(inner, credentials, profile))
        {
            await ScribeAsync(first, Keeper);

            _ = await new MemoryErasureRouteDriver(first.CreateClient()).EraseLexiconAsync(Keeper, null);
        }

        string replacement = NewKey();

        Assert.Equal(OsCredentialStoreStatus.Ok, credentials.Set(Service, Account, replacement).Status);

        await using (ArcanumWebApplicationFactory second = Host(inner, credentials, profile))
        {
            _ = await new MemoryErasureRouteDriver(second.CreateClient()).EraseSagaAsync(
                await MemoryErasureRouteDriver.InsertSagaAsync(second, Harbor));
        }

        return replacement;
    }

    /// <summary>Host 1 erases one Saga memory and one Lexicon entry; then the key is deleted.</summary>
    private static async Task LostKeySetupAsync(
        InMemoryOsCredentialStore inner,
        SecretAccessRecordingCredentialStore credentials,
        RestartableArcanumProfileFixture profile)
    {
        await using (ArcanumWebApplicationFactory first = Host(inner, credentials, profile))
        {
            MemoryErasureRouteDriver driver = new(first.CreateClient());

            _ = await driver.EraseSagaAsync(await MemoryErasureRouteDriver.InsertSagaAsync(first, Vault));

            await ScribeAsync(first, Keeper);

            _ = await driver.EraseLexiconAsync(Keeper, null);
        }

        Assert.Equal(OsCredentialStoreStatus.Ok, credentials.Delete(Service, Account).Status);
    }

    private static string NewKey() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static async Task<MemoryErasureStatusDto> StatusAsync(ArcanumWebApplicationFactory factory)
    {
        using HttpResponseMessage response = await factory.CreateAuthenticatedClient().GetAsync(StatusPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        AssertProtectedTuple(response);

        return await MemoryErasureRouteDriver.ReadDataAsync(response, ArcanumJsonContext.Default.ApiResponseMemoryErasureStatusDto);
    }

    private static async Task<MemoryErasureKeyResetPreflightDto> PrepareAsync(ArcanumWebApplicationFactory factory)
    {
        using HttpResponseMessage response = await factory.CreateAuthenticatedClient().PostAsync(PreparePath, content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        AssertProtectedTuple(response);

        return await MemoryErasureRouteDriver.ReadDataAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseMemoryErasureKeyResetPreflightDto);
    }

    private static async Task<MemoryErasureKeyResetResultDto> ResetAsync(ArcanumWebApplicationFactory factory, string token)
    {
        using HttpResponseMessage response = await new MemoryErasureRouteDriver(factory.CreateClient()).PostAsync(
            ResetPath,
            new MemoryErasureKeyResetRequest(token),
            ArcanumJsonContext.Default.MemoryErasureKeyResetRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        AssertProtectedTuple(response);

        return await MemoryErasureRouteDriver.ReadDataAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseMemoryErasureKeyResetResultDto);
    }

    private static StringContent ResetBody(string token) =>
        new(
            System.Text.Json.JsonSerializer.Serialize(
                new MemoryErasureKeyResetRequest(token),
                ArcanumJsonContext.Default.MemoryErasureKeyResetRequest),
            Encoding.UTF8,
            "application/json");

    private static async Task<MemoryErasureScrubResultDto> ScrubAsync(ArcanumWebApplicationFactory factory)
    {
        using HttpResponseMessage response = await factory.CreateAuthenticatedClient().PostAsync(ScrubPath, content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        AssertProtectedTuple(response);

        return await MemoryErasureRouteDriver.ReadDataAsync(response, ArcanumJsonContext.Default.ApiResponseMemoryErasureScrubResultDto);
    }

    /// <summary>Asks the Saga erase route to prepare one memory, as the CLI does after its show.</summary>
    private static async Task<HttpResponseMessage> PrepareSagaEraseAsync(ArcanumWebApplicationFactory factory, string memoryId)
    {
        SagaMemoryDetail detail;

        using (HttpResponseMessage shown = await factory.CreateAuthenticatedClient().GetAsync($"/api/memory/saga/{memoryId}"))
        {
            Assert.Equal(HttpStatusCode.OK, shown.StatusCode);

            detail = await MemoryErasureRouteDriver.ReadDataAsync(shown, ArcanumJsonContext.Default.ApiResponseSagaMemoryDetail);
        }

        return await new MemoryErasureRouteDriver(factory.CreateClient()).PostAsync(
            "/api/memory/saga/erase/prepare",
            new SagaErasePrepareRequest(memoryId, detail.ContentHash, detail.Claim?.CurrentVersionId, Guid.NewGuid()),
            ArcanumJsonContext.Default.SagaErasePrepareRequest);
    }

    /// <summary>Posts with the test API key and requires the refusal it names, with the protected tuple.</summary>
    private static async Task AssertRefusedAsync(
        ArcanumWebApplicationFactory factory,
        string path,
        HttpContent? content,
        HttpStatusCode status,
        string code)
    {
        using HttpResponseMessage refused = await factory.CreateAuthenticatedClient().PostAsync(path, content);

        Assert.Equal(status, refused.StatusCode);

        Assert.Equal(code, await MemoryErasureRouteDriver.ReadErrorCodeAsync(refused));

        AssertProtectedTuple(refused);
    }

    private static void AssertStores(
        IReadOnlyList<MemoryErasureStoreCountsDto> stores,
        (long Fingerprints, long Unverifiable, long Receipts) covenant,
        (long Fingerprints, long Unverifiable, long Receipts) saga,
        (long Fingerprints, long Unverifiable, long Receipts) lexicon) =>
        Assert.Equal<MemoryErasureStoreCountsDto>(
            [
                new(MemoryReviewStore.Covenant, covenant.Fingerprints, covenant.Unverifiable, covenant.Receipts),
                new(MemoryReviewStore.Saga, saga.Fingerprints, saga.Unverifiable, saga.Receipts),
                new(MemoryReviewStore.Lexicon, lexicon.Fingerprints, lexicon.Unverifiable, lexicon.Receipts),
            ],
            stores);

    /// <summary>Scribes one Global entry through the host's own Lexicon service, and requires that it landed.</summary>
    private static async Task ScribeAsync(ArcanumWebApplicationFactory factory, string name)
    {
        Result<LexiconEntryDto> scribed = await ScribeResultAsync(factory, name);

        Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : null);
    }

    /// <summary>The same scribe, reporting what the chokepoint decided.</summary>
    private static async Task<Result<LexiconEntryDto>> ScribeResultAsync(ArcanumWebApplicationFactory factory, string name)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<ILexiconService>()
            .UpsertAsync(name, "Person", ["keeps the vault key"], LexiconScope.Global, CancellationToken.None);
    }

    /// <summary>One integer read from the host's Grimoire. Assertion-only.</summary>
    private static async Task<long> ScalarAsync(ArcanumWebApplicationFactory factory, string sql)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync();
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The tuple every erasure response carries whatever its status (API §8.29, §8.35).</summary>
    private static void AssertProtectedTuple(HttpResponseMessage response)
    {
        Assert.Equal("no-store, private", response.Headers.CacheControl!.ToString());

        Assert.Equal("no-cache", Assert.Single(response.Headers.GetValues("Pragma")));

        Assert.Equal("0", Assert.Single(response.Content.Headers.GetValues("Expires")));

        Assert.Null(response.Headers.ETag);

        Assert.False(response.Content.Headers.Contains("Last-Modified"));
    }
}
