using System.Buffers.Text;

using System.Reflection;

using System.Security.Cryptography;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Hosting;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Memory;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

using RetroDownfall.Arcanum.Infrastructure.Lexicon;

using RetroDownfall.Arcanum.Infrastructure.Memory;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The erasure key lives in the OS credential store, one latched keyring per process.
/// </summary>
/// <remarks>
/// Automatic callers read the latch and never write; operator calls re-probe anything short of
/// <see cref="MemoryErasureKeyState.Present"/> and ask a present latch whether the key's item still
/// exists; creation happens once, under the keyring's lock, and only after a proven absence with no
/// evidence rows. Every store here is a fake over
/// <see cref="InMemoryOsCredentialStore"/> or a script, so no test reaches the real keychain.
/// </remarks>
public sealed class MemoryErasureKeyringTests
{
    private const string Service = "arcanum";

    private const string Account = "memory-erasure-fingerprint-key";

    private static readonly byte[] FixedKey = [.. Enumerable.Range(0, 32).Select(static value => (byte)value)];

    private static readonly byte[] OtherKey = [.. Enumerable.Range(0, 32).Select(static value => (byte)(0xA0 ^ value))];

    [Fact]
    public void Construction_latch_and_copy_perform_no_credential_io()
    {
        ForbiddenCredentialStore store = new();

        using MemoryErasureKeyring keyring = new(store);

        MemoryErasureKeyLatch latch = keyring.Latch;

        Assert.Equal(MemoryErasureKeyState.Unresolved, latch.State);

        Assert.Null(latch.KeyId);

        Assert.Null(keyring.TryCopyLatched());

        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public void Automatic_open_of_an_absent_account_latches_absent_and_never_writes()
    {
        CountingCredentialStore store = new(new InMemoryOsCredentialStore());

        using MemoryErasureKeyring keyring = new(store);

        MemoryErasureKeyOpenResult first = keyring.OpenExisting(MemoryErasureKeyProbe.UseLatched);

        MemoryErasureKeyOpenResult second = keyring.OpenExisting(MemoryErasureKeyProbe.UseLatched);

        Assert.Equal(MemoryErasureKeyState.Absent, first.State);

        Assert.Null(first.Key);

        Assert.Equal(MemoryErasureKeyState.Absent, second.State);

        Assert.Null(second.Key);

        Assert.Equal(MemoryErasureKeyState.Absent, keyring.Latch.State);

        Assert.Equal(1, store.TryGetCount);

        Assert.Equal(0, store.SetCount);

        Assert.Equal(0, store.DeleteCount);
    }

    [Theory]
    [InlineData("Unavailable")]
    [InlineData("Failed")]
    [InlineData("IOException")]
    [InlineData("UnauthorizedAccessException")]
    [InlineData("InvalidOperationException")]
    [InlineData("NotSupportedException")]
    public void Unavailable_failed_or_throwing_store_latches_unavailable_for_automatic_callers(string failure)
    {
        ScriptedCredentialStore store = new();

        _ = failure switch
        {
            "Unavailable" => store.Read(OsCredentialStoreResult.Unavailable("test backend unavailable")),
            "Failed" => store.Read(OsCredentialStoreResult.Failed("test backend failure")),
            "IOException" => store.ReadThrows(new IOException("test")),
            "UnauthorizedAccessException" => store.ReadThrows(new UnauthorizedAccessException("test")),
            "InvalidOperationException" => store.ReadThrows(new InvalidOperationException("test")),
            "NotSupportedException" => store.ReadThrows(new NotSupportedException("test")),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };

        using MemoryErasureKeyring keyring = new(store);

        MemoryErasureKeyOpenResult first = keyring.OpenExisting(MemoryErasureKeyProbe.UseLatched);

        Assert.Equal(MemoryErasureKeyState.Unavailable, first.State);

        Assert.Null(first.Key);

        MemoryErasureKeyOpenResult second = keyring.OpenExisting(MemoryErasureKeyProbe.UseLatched);

        Assert.Equal(MemoryErasureKeyState.Unavailable, second.State);

        Assert.Null(second.Key);

        Assert.Equal(MemoryErasureKeyState.Unavailable, keyring.Latch.State);

        Assert.Null(keyring.TryCopyLatched());

        Assert.Equal(1, store.TryGetCount);

        Assert.Equal(0, store.SetCount);
    }

    [Fact]
    public void Operator_reprobe_recovers_and_publishes_present_to_automatic_callers()
    {
        ScriptedCredentialStore store = new ScriptedCredentialStore()
            .Read(OsCredentialStoreResult.Unavailable("test backend unavailable"))
            .Read(OsCredentialStoreResult.Ok(Base64Url.EncodeToString(FixedKey)));

        using MemoryErasureKeyring keyring = new(store);

        Assert.Equal(
            MemoryErasureKeyState.Unavailable,
            keyring.OpenExisting(MemoryErasureKeyProbe.UseLatched).State);

        MemoryErasureKeyOpenResult reprobed = keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        using MemoryErasureKey? reprobedKey = reprobed.Key;

        Assert.Equal(MemoryErasureKeyState.Present, reprobed.State);

        Assert.NotNull(reprobedKey);

        Assert.Equal(MemoryErasureKeyState.Present, keyring.Latch.State);

        Assert.Equal(IndependentKeyId(FixedKey), keyring.Latch.KeyId);

        using MemoryErasureKey? latched = keyring.TryCopyLatched();

        Assert.NotNull(latched);

        MemoryErasureKeyOpenResult automatic = keyring.OpenExisting(MemoryErasureKeyProbe.UseLatched);

        using MemoryErasureKey? automaticKey = automatic.Key;

        Assert.Equal(MemoryErasureKeyState.Present, automatic.State);

        Assert.NotNull(automaticKey);

        Assert.Equal(2, store.TryGetCount);

        Assert.Equal(0, store.SetCount);
    }

    public static TheoryData<string> MalformedValues()
    {
        string canonical = Base64Url.EncodeToString(FixedKey);

        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

        // Thirty-two bytes leave two unused low bits in the final character. Setting one yields a
        // spelling a lenient decoder accepts and a canonical encoder never writes.
        char nonCanonicalTail = alphabet[alphabet.IndexOf(canonical[^1], StringComparison.Ordinal) | 1];

        return new TheoryData<string>
        {
            string.Empty,
            canonical + "=",
            canonical[..42],
            "+" + canonical[1..],
            "/" + canonical[1..],
            canonical[..42] + nonCanonicalTail,
        };
    }

    [Theory]
    [MemberData(nameof(MalformedValues))]
    public void Malformed_values_are_reported_and_never_overwritten(string stored)
    {
        InMemoryOsCredentialStore inner = new();

        _ = inner.Set(Service, Account, stored);

        CountingCredentialStore store = new(inner);

        using MemoryErasureKeyring keyring = new(store);

        MemoryErasureKeyOpenResult reprobed = keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        MemoryErasureKeyOpenResult opened = keyring.OpenOrCreate(evidenceRowsExist: false);

        MemoryErasureKeyOpenResult reset = keyring.CreateForReset();

        Assert.Equal(MemoryErasureKeyState.Malformed, reprobed.State);

        Assert.Null(reprobed.Key);

        Assert.Equal(MemoryErasureKeyState.Malformed, opened.State);

        Assert.Null(opened.Key);

        Assert.Equal(MemoryErasureKeyState.Malformed, reset.State);

        Assert.Null(reset.Key);

        Assert.Equal(MemoryErasureKeyState.Malformed, keyring.Latch.State);

        Assert.Equal(0, store.SetCount);

        Assert.Equal(0, store.DeleteCount);

        Assert.Equal(stored, inner.TryGet(Service, Account).Value);
    }

    [Fact]
    public void OpenOrCreate_writes_canonical_unpadded_base64url_reads_back_and_publishes_present()
    {
        InMemoryOsCredentialStore inner = new();

        CountingCredentialStore store = new(inner);

        using MemoryErasureKeyring keyring = new(store);

        MemoryErasureKeyOpenResult created = keyring.OpenOrCreate(evidenceRowsExist: false);

        using MemoryErasureKey? key = created.Key;

        Assert.Equal(MemoryErasureKeyState.Present, created.State);

        Assert.NotNull(key);

        OsCredentialStoreResult stored = inner.TryGet(Service, Account);

        Assert.Equal(OsCredentialStoreStatus.Ok, stored.Status);

        string value = Assert.IsType<string>(stored.Value);

        Assert.Equal(43, value.Length);

        byte[] decoded = Base64Url.DecodeFromChars(value);

        Assert.Equal(32, decoded.Length);

        Assert.Equal(value, Base64Url.EncodeToString(decoded));

        byte[] expectedKeyId = IndependentKeyId(decoded);

        Assert.Equal(expectedKeyId, key.KeyId.ToArray());

        Assert.Equal(MemoryErasureKeyState.Present, keyring.Latch.State);

        Assert.Equal(expectedKeyId, keyring.Latch.KeyId);

        Assert.Equal(1, store.SetCount);

        Assert.Equal(2, store.TryGetCount);
    }

    [Fact]
    public void OpenOrCreate_refuses_to_mint_when_evidence_rows_exist()
    {
        CountingCredentialStore store = new(new InMemoryOsCredentialStore());

        using MemoryErasureKeyring keyring = new(store);

        MemoryErasureKeyOpenResult opened = keyring.OpenOrCreate(evidenceRowsExist: true);

        Assert.Equal(MemoryErasureKeyState.Absent, opened.State);

        Assert.Null(opened.Key);

        Assert.Equal(MemoryErasureKeyState.Absent, keyring.Latch.State);

        Assert.Equal(0, store.SetCount);
    }

    [Fact]
    public void A_readback_mismatch_is_unavailable_and_writes_once()
    {
        ScriptedCredentialStore store = new ScriptedCredentialStore()
            .Read(OsCredentialStoreResult.NotFound())
            .Read(OsCredentialStoreResult.Ok(Base64Url.EncodeToString(OtherKey)));

        using MemoryErasureKeyring keyring = new(store);

        MemoryErasureKeyOpenResult opened = keyring.OpenOrCreate(evidenceRowsExist: false);

        Assert.Equal(MemoryErasureKeyState.Unavailable, opened.State);

        Assert.Null(opened.Key);

        Assert.Equal(1, store.SetCount);

        Assert.Equal(2, store.TryGetCount);

        Assert.Equal(MemoryErasureKeyState.Unavailable, keyring.Latch.State);

        Assert.Null(keyring.Latch.KeyId);

        Assert.Null(keyring.TryCopyLatched());
    }

    /// <summary>
    /// Two first-ever erasures racing on a fresh installation create exactly one key.
    /// </summary>
    [Fact]
    public void Concurrent_first_creates_write_exactly_once()
    {
        const int callers = 8;

        CountingCredentialStore store = new(
            new InMemoryOsCredentialStore(),
            readDelay: TimeSpan.FromMilliseconds(50));

        using MemoryErasureKeyring keyring = new(store);

        using Barrier start = new(callers);

        MemoryErasureKeyOpenResult?[] results = new MemoryErasureKeyOpenResult?[callers];

        Exception?[] faults = new Exception?[callers];

        Thread[] threads = [.. Enumerable.Range(0, callers).Select(index => new Thread(() =>
        {
            try
            {
                start.SignalAndWait();

                results[index] = keyring.OpenOrCreate(evidenceRowsExist: false);
            }
            catch (Exception exception)
            {
                faults[index] = exception;
            }
        })
        {
            IsBackground = true,
        })];

        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        }

        try
        {
            Assert.All(faults, static fault => Assert.Null(fault));

            Assert.Equal(1, store.SetCount);

            Assert.Equal(1, store.MaxConcurrentCalls);

            Assert.All(results, static result => Assert.Equal(MemoryErasureKeyState.Present, result!.State));

            _ = Assert.Single(results
                .Select(static result => Convert.ToHexString(result!.Key!.KeyId))
                .Distinct(StringComparer.Ordinal));
        }
        finally
        {
            foreach (MemoryErasureKeyOpenResult? result in results)
            {
                result?.Key?.Dispose();
            }
        }
    }

    /// <summary>
    /// Readers of the latch never wait behind a probe another caller has in flight.
    /// </summary>
    /// <remarks>
    /// A probe can sit behind a keychain prompt for as long as the operator leaves it there, and the
    /// latch is read while Covenant leases are held. Waiting for the lock that probe holds would be
    /// waiting for keychain I/O under a lease.
    /// </remarks>
    [Fact]
    public async Task Latch_and_copy_never_wait_behind_an_inflight_probe()
    {
        TimeSpan prompt = TimeSpan.FromSeconds(5);

        InMemoryOsCredentialStore inner = new();

        _ = inner.Set(Service, Account, Base64Url.EncodeToString(FixedKey));

        using GatedCredentialStore store = new(inner);

        using MemoryErasureKeyring keyring = new(store);

        Task<MemoryErasureKeyOpenResult> reprobe = Task.Run(
            () => keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe));

        Assert.True(await store.WaitUntilHeldAsync());

        MemoryErasureKeyLatch unresolved = await Task.Run(() => keyring.Latch).WaitAsync(prompt);

        Assert.Equal(MemoryErasureKeyState.Unresolved, unresolved.State);

        Assert.Null(await Task.Run(keyring.TryCopyLatched).WaitAsync(prompt));

        store.Release();

        using (MemoryErasureKey? opened = (await reprobe.WaitAsync(TimeSpan.FromSeconds(30))).Key)
        {
            Assert.NotNull(opened);
        }

        store.Hold();

        Task<MemoryErasureKeyOpenResult> reset = Task.Run(keyring.CreateForReset);

        Assert.True(await store.WaitUntilHeldAsync());

        MemoryErasureKeyLatch present = await Task.Run(() => keyring.Latch).WaitAsync(prompt);

        Assert.Equal(MemoryErasureKeyState.Present, present.State);

        Assert.Equal(IndependentKeyId(FixedKey), present.KeyId);

        using (MemoryErasureKey? during = await Task.Run(keyring.TryCopyLatched).WaitAsync(prompt))
        {
            Assert.NotNull(during);

            Assert.True(during.HasKeyId(IndependentKeyId(FixedKey)));
        }

        store.Release();

        using MemoryErasureKey? kept = (await reset.WaitAsync(TimeSpan.FromSeconds(30))).Key;

        Assert.NotNull(kept);
    }

    /// <summary>
    /// A resolved latch answers an automatic open without waiting behind an operator's probe that is
    /// parked inside the credential store, and a present one answers an operator open the same way over
    /// a store that cannot be asked whether the item exists.
    /// </summary>
    /// <remarks>
    /// Taking the keyring's lock for an answer that needs no I/O would put every automatic caller,
    /// including the ones that hold a Covenant lease, behind a keychain prompt for as long as the
    /// operator leaves it up. The operator's own probe is the one that is allowed to wait, and so is an
    /// operator open that has to ask the store whether a present key's item still exists: this fixture's
    /// store has no such probe, which is what lets its operator open answer from the latch.
    /// </remarks>
    [Theory]
    [InlineData("Absent")]
    [InlineData("Malformed")]
    [InlineData("Present")]
    public async Task Open_existing_answers_from_a_resolved_latch_without_waiting_behind_an_inflight_probe(string resolved)
    {
        TimeSpan prompt = TimeSpan.FromSeconds(5);

        InMemoryOsCredentialStore inner = new();

        _ = resolved switch
        {
            "Absent" => OsCredentialStoreResult.NotFound(),
            "Malformed" => inner.Set(Service, Account, "not base64url"),
            "Present" => inner.Set(Service, Account, Base64Url.EncodeToString(FixedKey)),
            _ => throw new ArgumentOutOfRangeException(nameof(resolved)),
        };

        MemoryErasureKeyState expected = Enum.Parse<MemoryErasureKeyState>(resolved);

        using GatedCredentialStore store = new(inner);

        using MemoryErasureKeyring keyring = new(store);

        try
        {
            // Resolve the latch with the store open, then take the one stale signal that read left.
            store.Release();

            using (MemoryErasureKey? first = keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe).Key)
            {
                Assert.Equal(expected, keyring.Latch.State);
            }

            Assert.True(await store.WaitUntilHeldAsync());

            // Park an operator's probe inside the store, holding whatever lock the keyring takes for it.
            store.Hold();

            Func<MemoryErasureKeyOpenResult> operatorProbe = resolved == "Present"
                ? () => keyring.CreateForReset()
                : () => keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe);

            Task<MemoryErasureKeyOpenResult> parked = Task.Run(operatorProbe);

            Assert.True(await store.WaitUntilHeldAsync());

            MemoryErasureKeyOpenResult automatic = await Task
                .Run(() => keyring.OpenExisting(MemoryErasureKeyProbe.UseLatched))
                .WaitAsync(prompt);

            using MemoryErasureKey? automaticKey = automatic.Key;

            Assert.Equal(expected, automatic.State);

            if (resolved == "Present")
            {
                Assert.NotNull(automaticKey);

                Assert.True(automaticKey.HasKeyId(IndependentKeyId(FixedKey)));

                // An operator open of a present latch over a store that cannot be asked whether the item
                // exists is answered from the latch too, and never probes.
                MemoryErasureKeyOpenResult operatorOpen = await Task
                    .Run(() => keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe))
                    .WaitAsync(prompt);

                using MemoryErasureKey? operatorKey = operatorOpen.Key;

                Assert.Equal(MemoryErasureKeyState.Present, operatorOpen.State);

                Assert.NotNull(operatorKey);

                Assert.True(operatorKey.HasKeyId(IndependentKeyId(FixedKey)));

                Assert.NotSame(automaticKey, operatorKey);
            }
            else
            {
                Assert.Null(automaticKey);
            }

            Assert.False(parked.IsCompleted);

            store.Release();

            using MemoryErasureKey? finished = (await parked.WaitAsync(TimeSpan.FromSeconds(30))).Key;
        }
        finally
        {
            // Releases rather than disposes, so a probe still parked on a failing run can finish before
            // the keyring that holds its lock is disposed.
            store.Release();
        }
    }

    /// <summary>
    /// A copy handed out while the latch is republished is never part-zeroed.
    /// </summary>
    /// <remarks>
    /// Publishing a key swaps in a new snapshot and then zeroes the one it replaced, so a reader that
    /// copied from the old snapshot may copy zeroes. The copy-and-recheck loop discards such a copy. A
    /// republish with an unchanged key is the sharpest probe, because the replaced bytes and the new
    /// ones are equal and only the zeroing differs: a reader that returns a torn copy is caught by its
    /// key id, which is computed from the copied bytes.
    /// </remarks>
    [Fact]
    public async Task A_copy_taken_while_the_latch_is_republished_is_never_torn()
    {
        const int readers = 4;

        const int republishes = 100_000;

        InMemoryOsCredentialStore inner = new();

        _ = inner.Set(Service, Account, Base64Url.EncodeToString(FixedKey));

        using MemoryErasureKeyring keyring = new(inner);

        using (MemoryErasureKey? opened = keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe).Key)
        {
            Assert.NotNull(opened);
        }

        byte[] expectedKeyId = IndependentKeyId(FixedKey);

        using CancellationTokenSource stop = new();

        long copies = 0;

        long torn = 0;

        Task[] readerTasks =
        [
            .. Enumerable.Range(0, readers).Select(reader => Task.Factory.StartNew(
                () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        using MemoryErasureKey? copy = keyring.TryCopyLatched();

                        _ = Interlocked.Increment(ref copies);

                        if (copy is null || !copy.HasKeyId(expectedKeyId))
                        {
                            _ = Interlocked.Increment(ref torn);
                        }
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)),
        ];

        try
        {
            for (int republish = 0; republish < republishes; republish++)
            {
                // A reset re-reads the account and publishes what it finds, replacing the snapshot the
                // readers are copying from with one that holds the same key.
                using MemoryErasureKey? kept = keyring.CreateForReset().Key;

                Assert.NotNull(kept);
            }
        }
        finally
        {
            await stop.CancelAsync();

            await Task.WhenAll(readerTasks).WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.True(Interlocked.Read(ref copies) > 0, "No reader ever copied the key.");

        Assert.Equal(0, Interlocked.Read(ref torn));
    }

    /// <summary>
    /// Every way a create can fail leaves the latch Unavailable after exactly one write, and nothing
    /// asks the store again for an automatic caller.
    /// </summary>
    [Theory]
    [InlineData("SetUnavailable", 1)]
    [InlineData("SetFailed", 1)]
    [InlineData("SetThrows", 1)]
    [InlineData("ReadBackNotFound", 2)]
    [InlineData("ReadBackUnavailable", 2)]
    [InlineData("ReadBackMalformed", 2)]
    [InlineData("ReadBackThrows", 2)]
    public void A_failed_create_latches_unavailable_after_one_write(string failure, int reads)
    {
        ScriptedCredentialStore store = new ScriptedCredentialStore().Read(OsCredentialStoreResult.NotFound());

        _ = failure switch
        {
            "SetUnavailable" => store.Write(OsCredentialStoreResult.Unavailable("test backend unavailable")),
            "SetFailed" => store.Write(OsCredentialStoreResult.Failed("test backend failure")),
            "SetThrows" => store.WriteThrows(new IOException("test")),
            "ReadBackNotFound" => store.Read(OsCredentialStoreResult.NotFound()),
            "ReadBackUnavailable" => store.Read(OsCredentialStoreResult.Unavailable("test backend unavailable")),
            "ReadBackMalformed" => store.Read(OsCredentialStoreResult.Ok("not base64url")),
            "ReadBackThrows" => store.ReadThrows(new IOException("test")),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };

        using MemoryErasureKeyring keyring = new(store);

        MemoryErasureKeyOpenResult opened = keyring.OpenOrCreate(evidenceRowsExist: false);

        Assert.Equal(MemoryErasureKeyState.Unavailable, opened.State);

        Assert.Null(opened.Key);

        Assert.Equal(MemoryErasureKeyState.Unavailable, keyring.Latch.State);

        Assert.Null(keyring.Latch.KeyId);

        Assert.Null(keyring.TryCopyLatched());

        Assert.Equal(1, store.SetCount);

        Assert.Equal(reads, store.TryGetCount);

        Assert.Equal(0, store.DeleteCount);

        // The failure is remembered: an automatic caller is told so without another read or write.
        MemoryErasureKeyOpenResult again = keyring.OpenExisting(MemoryErasureKeyProbe.UseLatched);

        Assert.Equal(MemoryErasureKeyState.Unavailable, again.State);

        Assert.Null(again.Key);

        Assert.Equal(1, store.SetCount);

        Assert.Equal(reads, store.TryGetCount);
    }

    [Fact]
    public void Two_keyrings_sharing_one_account_converge_on_the_first_key()
    {
        CountingCredentialStore store = new(new InMemoryOsCredentialStore());

        using MemoryErasureKeyring first = new(store);

        using MemoryErasureKeyring second = new(store);

        using MemoryErasureKey? firstKey = first.OpenOrCreate(evidenceRowsExist: false).Key;

        using MemoryErasureKey? secondKey = second.OpenOrCreate(evidenceRowsExist: false).Key;

        Assert.NotNull(firstKey);

        Assert.NotNull(secondKey);

        Assert.Equal(firstKey.KeyId.ToArray(), secondKey.KeyId.ToArray());

        Assert.Equal(1, store.SetCount);
    }

    /// <summary>
    /// A second test home that overwrites the shared account does not change a latched key, and
    /// anything written under the old key is detectably foreign to a keyring that reads the new one.
    /// </summary>
    /// <remarks>
    /// An overwritten item still exists, so the operator's presence question answers from the latch too.
    /// Only a deleted item is reported, by
    /// <see cref="An_operator_open_of_a_present_latch_asks_whether_the_item_exists_and_an_automatic_open_never_does"/>.
    /// </remarks>
    [Fact]
    public void An_overwritten_account_keeps_the_latched_key_and_its_key_id_mismatch_is_detectable()
    {
        InMemoryOsCredentialStore inner = new();

        CountingCredentialStore store = new(inner);

        using MemoryErasureKeyring first = new(store);

        byte[] original;

        using (MemoryErasureKey? created = first.OpenOrCreate(evidenceRowsExist: false).Key)
        {
            Assert.NotNull(created);

            original = created.KeyId.ToArray();
        }

        _ = inner.Set(Service, Account, Base64Url.EncodeToString(OtherKey));

        Assert.Equal(MemoryErasureKeyState.Present, first.Latch.State);

        Assert.Equal(original, first.Latch.KeyId);

        using (MemoryErasureKey? latched = first.OpenExisting(MemoryErasureKeyProbe.Reprobe).Key)
        {
            Assert.NotNull(latched);

            Assert.True(latched.HasKeyId(original));
        }

        using MemoryErasureKeyring fresh = new(store);

        using MemoryErasureKey? replacement = fresh.OpenExisting(MemoryErasureKeyProbe.Reprobe).Key;

        Assert.NotNull(replacement);

        Assert.False(replacement.HasKeyId(original));

        Assert.True(replacement.HasKeyId(IndependentKeyId(OtherKey)));
    }

    [Theory]
    [InlineData("Present")]
    [InlineData("NotFound")]
    [InlineData("Unavailable")]
    [InlineData("Malformed")]
    public void CreateForReset_keeps_creates_or_refuses_by_what_the_store_reports(string storeState)
    {
        switch (storeState)
        {
            case "Present":
            {
                InMemoryOsCredentialStore inner = new();

                _ = inner.Set(Service, Account, Base64Url.EncodeToString(FixedKey));

                CountingCredentialStore store = new(inner);

                using MemoryErasureKeyring keyring = new(store);

                using (MemoryErasureKey? latched = keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe).Key)
                {
                    Assert.NotNull(latched);
                }

                MemoryErasureKeyOpenResult reset = keyring.CreateForReset();

                using MemoryErasureKey? kept = reset.Key;

                Assert.Equal(MemoryErasureKeyState.Present, reset.State);

                Assert.NotNull(kept);

                Assert.Equal(IndependentKeyId(FixedKey), kept.KeyId.ToArray());

                // A reset always asks the store, even over a Present latch.
                Assert.Equal(2, store.TryGetCount);

                Assert.Equal(0, store.SetCount);

                Assert.Equal(0, store.DeleteCount);

                break;
            }

            case "NotFound":
            {
                InMemoryOsCredentialStore inner = new();

                CountingCredentialStore store = new(inner);

                using MemoryErasureKeyring keyring = new(store);

                MemoryErasureKeyOpenResult reset = keyring.CreateForReset();

                using MemoryErasureKey? created = reset.Key;

                Assert.Equal(MemoryErasureKeyState.Present, reset.State);

                Assert.NotNull(created);

                byte[] stored = Base64Url.DecodeFromChars(inner.TryGet(Service, Account).Value);

                Assert.Equal(IndependentKeyId(stored), created.KeyId.ToArray());

                Assert.Equal(MemoryErasureKeyState.Present, keyring.Latch.State);

                Assert.Equal(1, store.SetCount);

                Assert.Equal(0, store.DeleteCount);

                break;
            }

            case "Unavailable":
            {
                ScriptedCredentialStore store = new ScriptedCredentialStore()
                    .Read(OsCredentialStoreResult.Unavailable("test backend unavailable"));

                using MemoryErasureKeyring keyring = new(store);

                MemoryErasureKeyOpenResult reset = keyring.CreateForReset();

                Assert.Equal(MemoryErasureKeyState.Unavailable, reset.State);

                Assert.Null(reset.Key);

                Assert.Equal(MemoryErasureKeyState.Unavailable, keyring.Latch.State);

                Assert.Equal(0, store.SetCount);

                Assert.Equal(0, store.DeleteCount);

                break;
            }

            case "Malformed":
            {
                InMemoryOsCredentialStore inner = new();

                _ = inner.Set(Service, Account, "not-an-erasure-key");

                CountingCredentialStore store = new(inner);

                using MemoryErasureKeyring keyring = new(store);

                MemoryErasureKeyOpenResult reset = keyring.CreateForReset();

                Assert.Equal(MemoryErasureKeyState.Malformed, reset.State);

                Assert.Null(reset.Key);

                Assert.Equal(0, store.SetCount);

                Assert.Equal(0, store.DeleteCount);

                Assert.Equal("not-an-erasure-key", inner.TryGet(Service, Account).Value);

                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(storeState));
        }
    }

    [Fact]
    public void Returned_keys_are_independent_copies()
    {
        CountingCredentialStore store = new(new InMemoryOsCredentialStore());

        using MemoryErasureKeyring keyring = new(store);

        MemoryErasureIdentity identity = MemoryErasureIdentity.ForLexicon(null, "independent copy");

        using MemoryErasureKey? created = keyring.OpenOrCreate(evidenceRowsExist: false).Key;

        Assert.NotNull(created);

        byte[] expected = created.Fingerprint(identity);

        MemoryErasureKey? discarded = keyring.TryCopyLatched();

        Assert.NotNull(discarded);

        discarded.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => discarded.Fingerprint(identity));

        using (MemoryErasureKey? copy = keyring.TryCopyLatched())
        {
            Assert.NotNull(copy);

            Assert.NotSame(created, copy);

            Assert.Equal(expected, copy.Fingerprint(identity));
        }

        int reads = store.TryGetCount;

        int writes = store.SetCount;

        keyring.Dispose();

        foreach (MemoryErasureKeyOpenResult result in (MemoryErasureKeyOpenResult[])
                 [
                     keyring.OpenExisting(MemoryErasureKeyProbe.UseLatched),
                     keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe),
                     keyring.OpenOrCreate(evidenceRowsExist: false),
                     keyring.CreateForReset(),
                 ])
        {
            Assert.Equal(MemoryErasureKeyState.Unavailable, result.State);

            Assert.Null(result.Key);
        }

        Assert.Equal(MemoryErasureKeyState.Unavailable, keyring.Latch.State);

        Assert.Null(keyring.Latch.KeyId);

        Assert.Null(keyring.TryCopyLatched());

        Assert.Equal(reads, store.TryGetCount);

        Assert.Equal(writes, store.SetCount);

        Assert.Equal(0, store.DeleteCount);

        // The keyring zeroed its own cache; a copy it handed out earlier is the caller's.
        Assert.Equal(expected, created.Fingerprint(identity));
    }

    [Fact]
    public async Task Host_composition_registers_one_keyring_behind_both_ports()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        builder.Services.AddSingleton<IOsCredentialStore>(new InMemoryOsCredentialStore());

        builder.Services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        AssertSingleSingleton<MemoryErasureKeyring>(builder.Services);

        AssertSingleSingleton<IMemoryErasureKeyProvider>(builder.Services);

        AssertSingleSingleton<IMemoryErasureKeyCreator>(builder.Services);

        await using ServiceProvider provider = builder.Services.BuildServiceProvider();

        IMemoryErasureKeyProvider keyProvider = provider.GetRequiredService<IMemoryErasureKeyProvider>();

        Assert.Same(keyProvider, provider.GetRequiredService<IMemoryErasureKeyCreator>());

        Assert.Same(keyProvider, provider.GetRequiredService<MemoryErasureKeyring>());
    }

    [Fact]
    public async Task Cli_composition_resolves_the_provider_but_never_the_creator()
    {
        ServiceCollection services = [];

        services.AddLogging();

        services.AddSingleton<IOsCredentialStore>(new InMemoryOsCredentialStore());

        services.AddArcanumCliClientStack();

        _ = Assert.Single(
            services,
            static descriptor => descriptor.ServiceType == typeof(IMemoryErasureKeyProvider));

        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType == typeof(IMemoryErasureKeyCreator));

        await using ServiceProvider provider = services.BuildServiceProvider();

        _ = Assert.IsType<MemoryErasureKeyring>(provider.GetRequiredService<IMemoryErasureKeyProvider>());

        Assert.Null(provider.GetService<IMemoryErasureKeyCreator>());
    }

    /// <summary>
    /// The creator port, and the concrete keyring that implements it, are taken by a closed set of
    /// constructors, every one of them an erasure service the host alone composes.
    /// </summary>
    /// <remarks>
    /// The CLI and restore containers register the keyring as itself so each holds one latch, which
    /// leaves the concrete type resolvable there: nothing but the services below keeps a consumer from
    /// asking for it and creating a key. So a new constructor that takes either is a decision this list
    /// makes visible, and <see cref="Host_only_erasure_services_are_not_registered_in_the_cli_or_restore_compositions"/>
    /// holds the other half.
    /// </remarks>
    [Fact]
    public void Only_host_only_erasure_services_take_the_key_creator_or_the_concrete_keyring()
    {
        Assembly[] assemblies =
        [
            typeof(MemoryErasureKeyring).Assembly,
            typeof(ArcanumJsonContext).Assembly,
            typeof(ArcanumApiClient).Assembly,
        ];

        string[] consumers =
        [
            .. assemblies
                .SelectMany(static assembly => assembly.GetTypes())
                .Where(static type => !type.IsAbstract)
                .Where(static type => type
                    .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(static constructor => constructor.GetParameters().Any(static parameter =>
                        parameter.ParameterType == typeof(MemoryErasureKeyring)
                        || parameter.ParameterType == typeof(IMemoryErasureKeyCreator))))
                .Select(static type => type.Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(
            [
                nameof(CovenantEntryErasureService),
                nameof(LexiconErasureDependencies),
                nameof(MemoryErasureAdministration),
                nameof(SagaMemoryErasureService),
            ],
            consumers);
    }

    /// <summary>
    /// Neither the CLI stack nor the backup registration, which restore runs in, registers any of the
    /// services that take the creator or the concrete keyring, or any interface they answer to.
    /// </summary>
    [Fact]
    public void Host_only_erasure_services_are_not_registered_in_the_cli_or_restore_compositions()
    {
        Type[] hostOnly =
        [
            typeof(CovenantEntryErasureService),
            typeof(LexiconErasureDependencies),
            typeof(MemoryErasureAdministration),
            typeof(SagaMemoryErasureService),
        ];

        HashSet<Type> serviceTypes =
        [
            .. hostOnly,
            .. hostOnly
                .SelectMany(static type => type.GetInterfaces())
                .Where(static contract => contract.Namespace?.StartsWith("RetroDownfall.Arcanum", StringComparison.Ordinal) == true),
        ];

        // The set is only as good as the interfaces it found, so each port the services answer to is named.
        Assert.Contains(typeof(ISagaMemoryErasureService), serviceTypes);

        Assert.Contains(typeof(ICovenantEntryErasureService), serviceTypes);

        Assert.Contains(typeof(IMemoryErasureAdministration), serviceTypes);

        ServiceCollection cli = [];

        cli.AddLogging();

        cli.AddSingleton<IOsCredentialStore>(new InMemoryOsCredentialStore());

        cli.AddArcanumCliClientStack();

        ServiceCollection backup = [];

        backup.AddLogging();

        backup.AddSingleton<IOsCredentialStore>(new InMemoryOsCredentialStore());

        backup.AddArcanumBackup();

        foreach ((string composition, ServiceCollection services) in new[] { ("CLI stack", cli), ("backup", backup) })
        {
            Assert.True(
                services.Count > 0,
                $"The {composition} composition registered nothing.");

            Assert.Empty(
                services
                    .Where(descriptor => serviceTypes.Contains(descriptor.ServiceType)
                        || (descriptor.ImplementationType is { } implementation && hostOnly.Contains(implementation)))
                    .Select(static descriptor => descriptor.ServiceType.Name));
        }
    }

    [Fact]
    public async Task Composing_the_host_performs_no_credential_io()
    {
        ForbiddenCredentialStore store = new();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        builder.Services.AddSingleton<IOsCredentialStore>(store);

        builder.Services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        await using ServiceProvider provider = builder.Services.BuildServiceProvider();

        IMemoryErasureKeyProvider keyProvider = provider.GetRequiredService<IMemoryErasureKeyProvider>();

        _ = provider.GetRequiredService<IMemoryErasureKeyCreator>();

        MemoryErasureKeyLatch latch = keyProvider.Latch;

        Assert.Equal(MemoryErasureKeyState.Unresolved, latch.State);

        Assert.Null(latch.KeyId);

        Assert.Null(keyProvider.TryCopyLatched());

        Assert.Equal(0, store.Calls);
    }

    /// <summary>
    /// The reset's create path says whether it wrote the key: only a proven absence is a creation, and a
    /// key it finds, or an item it refuses, is not.
    /// </summary>
    [Fact]
    public void CreateForReset_reports_whether_it_created_the_key()
    {
        InMemoryOsCredentialStore inner = new();

        using (MemoryErasureKeyring absent = new(inner))
        {
            MemoryErasureKeyOpenResult created = absent.CreateForReset(out bool wrote);

            using MemoryErasureKey? key = created.Key;

            Assert.Equal(MemoryErasureKeyState.Present, created.State);

            Assert.True(wrote);
        }

        using (MemoryErasureKeyring present = new(inner))
        {
            MemoryErasureKeyOpenResult found = present.CreateForReset(out bool wrote);

            using MemoryErasureKey? key = found.Key;

            Assert.Equal(MemoryErasureKeyState.Present, found.State);

            Assert.False(wrote);
        }

        Assert.Equal(OsCredentialStoreStatus.Ok, inner.Set(Service, Account, "not base64url").Status);

        using (MemoryErasureKeyring malformed = new(inner))
        {
            MemoryErasureKeyOpenResult refused = malformed.CreateForReset(out bool wrote);

            Assert.Equal(MemoryErasureKeyState.Malformed, refused.State);

            Assert.False(wrote);
        }

        Assert.Equal("not base64url", inner.TryGet(Service, Account).Value);
    }

    /// <summary>
    /// The presence probe asks only whether the item exists: it reads no secret, never resolves or
    /// changes the latch, answers null for a store that cannot probe, and fails closed.
    /// </summary>
    [Fact]
    public void Presence_probe_reads_no_secret_and_leaves_the_latch_alone()
    {
        InMemoryOsCredentialStore inner = new();

        CountingCredentialStore store = new(inner);

        using (MemoryErasureKeyring absent = new(store))
        {
            Assert.Equal(OsCredentialStoreStatus.NotFound, absent.ProbePresence());

            Assert.Equal(MemoryErasureKeyState.Unresolved, absent.Latch.State);
        }

        Assert.Equal(OsCredentialStoreStatus.Ok, inner.Set(Service, Account, Base64Url.EncodeToString(FixedKey)).Status);

        using (MemoryErasureKeyring present = new(store))
        {
            Assert.Equal(OsCredentialStoreStatus.Ok, present.ProbePresence());

            Assert.Equal(MemoryErasureKeyState.Unresolved, present.Latch.State);

            Assert.Null(present.TryCopyLatched());
        }

        Assert.Equal(0, store.TryGetCount);

        Assert.Equal(0, store.SetCount);

        Assert.Equal(2, store.ProbeCount);

        ScriptedCredentialStore scripted = new();

        using (MemoryErasureKeyring withoutProbe = new(scripted))
        {
            Assert.Null(withoutProbe.ProbePresence());

            Assert.Equal(MemoryErasureKeyState.Unresolved, withoutProbe.Latch.State);
        }

        Assert.Equal(0, scripted.TryGetCount);

        using (MemoryErasureKeyring throwing = new(new ThrowingProbeCredentialStore()))
        {
            Assert.Equal(OsCredentialStoreStatus.Unavailable, throwing.ProbePresence());

            Assert.Equal(MemoryErasureKeyState.Unresolved, throwing.Latch.State);
        }

        MemoryErasureKeyring disposed = new(store);

        disposed.Dispose();

        Assert.Equal(OsCredentialStoreStatus.Unavailable, disposed.ProbePresence());

        Assert.Equal(2, store.ProbeCount);
    }

    /// <summary>
    /// An operator's open of a present latch asks the store whether the key's item still exists, and an
    /// automatic open never does.
    /// </summary>
    /// <remarks>
    /// The question is metadata only, so it reads no secret and cannot raise a keychain prompt. While the
    /// item stands, the operator is answered from the latch. Once it is gone the operator is told so and the
    /// absence is published, as any operator probe publishes what it finds; until then an automatic caller
    /// keeps the lock-free latch it always had.
    /// </remarks>
    [Fact]
    public void An_operator_open_of_a_present_latch_asks_whether_the_item_exists_and_an_automatic_open_never_does()
    {
        InMemoryOsCredentialStore inner = new();

        CountingCredentialStore store = new(inner);

        using MemoryErasureKeyring keyring = new(store);

        using (MemoryErasureKey? created = keyring.OpenOrCreate(evidenceRowsExist: false).Key)
        {
            Assert.NotNull(created);
        }

        int reads = store.TryGetCount;

        int probes = store.ProbeCount;

        using (MemoryErasureKey? automatic = keyring.OpenExisting(MemoryErasureKeyProbe.UseLatched).Key)
        {
            Assert.NotNull(automatic);
        }

        Assert.Equal(probes, store.ProbeCount);

        MemoryErasureKeyOpenResult standing = keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        using (MemoryErasureKey? operatorKey = standing.Key)
        {
            Assert.Equal(MemoryErasureKeyState.Present, standing.State);

            Assert.NotNull(operatorKey);
        }

        Assert.Equal(probes + 1, store.ProbeCount);

        Assert.Equal(reads, store.TryGetCount);

        _ = inner.Delete(Service, Account);

        // Nothing has asked yet, so the automatic caller still holds the key it was given.
        using (MemoryErasureKey? stale = keyring.OpenExisting(MemoryErasureKeyProbe.UseLatched).Key)
        {
            Assert.NotNull(stale);
        }

        MemoryErasureKeyOpenResult gone = keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        Assert.Equal(MemoryErasureKeyState.Absent, gone.State);

        Assert.Null(gone.Key);

        Assert.Equal(MemoryErasureKeyState.Absent, keyring.Latch.State);

        Assert.Null(keyring.TryCopyLatched());

        Assert.Equal(MemoryErasureKeyState.Absent, keyring.OpenExisting(MemoryErasureKeyProbe.UseLatched).State);

        // Only the creation wrote: neither the asking nor the finding of the absence wrote or deleted anything.
        Assert.Equal(1, store.SetCount);

        Assert.Equal(0, store.DeleteCount);
    }

    /// <summary>
    /// An automatic open of a present latch never waits behind an operator's presence question that is
    /// parked inside the credential store, whatever store it is.
    /// </summary>
    /// <remarks>
    /// The question takes the keyring's lock, and a store can sit behind a prompt for as long as the
    /// operator leaves it up, so the lock-free latch an automatic caller holds under a Covenant lease is
    /// what keeps that wait away from it.
    /// </remarks>
    [Fact]
    public async Task An_automatic_open_of_a_present_latch_never_waits_behind_an_operators_presence_question()
    {
        TimeSpan prompt = TimeSpan.FromSeconds(5);

        using GatedPresenceCredentialStore store = new(new InMemoryOsCredentialStore());

        using MemoryErasureKeyring keyring = new(store);

        using (MemoryErasureKey? created = keyring.OpenOrCreate(evidenceRowsExist: false).Key)
        {
            Assert.NotNull(created);
        }

        store.Hold();

        Task<MemoryErasureKeyOpenResult> parked = Task.Run(() => keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe));

        Assert.True(await store.WaitUntilHeldAsync());

        MemoryErasureKeyOpenResult automatic = await Task
            .Run(() => keyring.OpenExisting(MemoryErasureKeyProbe.UseLatched))
            .WaitAsync(prompt);

        using (MemoryErasureKey? automaticKey = automatic.Key)
        {
            Assert.Equal(MemoryErasureKeyState.Present, automatic.State);

            Assert.NotNull(automaticKey);
        }

        Assert.Equal(MemoryErasureKeyState.Present, (await Task.Run(() => keyring.Latch).WaitAsync(prompt)).State);

        Assert.False(parked.IsCompleted);

        store.Release();

        using MemoryErasureKey? answered = (await parked.WaitAsync(TimeSpan.FromSeconds(30))).Key;

        Assert.NotNull(answered);
    }

    /// <summary>
    /// A presence probe that cannot answer reports the operator's open unavailable, and leaves the key the
    /// latch holds, because a fault in asking says nothing about whether the item is gone.
    /// </summary>
    [Theory]
    [InlineData(OsCredentialStoreStatus.Unavailable)]
    [InlineData(OsCredentialStoreStatus.Failed)]
    public void A_presence_probe_that_cannot_answer_reports_unavailable_without_losing_the_latched_key(OsCredentialStoreStatus answer)
    {
        FaultablePresenceCredentialStore store = new(new InMemoryOsCredentialStore());

        using MemoryErasureKeyring keyring = new(store);

        using (MemoryErasureKey? created = keyring.OpenOrCreate(evidenceRowsExist: false).Key)
        {
            Assert.NotNull(created);
        }

        int reads = store.TryGetCount;

        store.Answer = answer;

        MemoryErasureKeyOpenResult asked = keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        Assert.Equal(MemoryErasureKeyState.Unavailable, asked.State);

        Assert.Null(asked.Key);

        Assert.Equal(MemoryErasureKeyState.Present, keyring.Latch.State);

        using (MemoryErasureKey? kept = keyring.TryCopyLatched())
        {
            Assert.NotNull(kept);
        }

        Assert.Equal(reads, store.TryGetCount);

        store.Answer = null;

        store.Throw = true;

        MemoryErasureKeyOpenResult thrown = keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        Assert.Equal(MemoryErasureKeyState.Unavailable, thrown.State);

        Assert.Equal(MemoryErasureKeyState.Present, keyring.Latch.State);

        Assert.Equal(reads, store.TryGetCount);
    }

    /// <summary>
    /// A prepare's open of a present latch whose item is gone mints nothing while evidence exists, which is
    /// a lost key, and mints a fresh key when none does, as it would on a proven absence.
    /// </summary>
    [Fact]
    public void OpenOrCreate_over_a_present_latch_whose_item_is_gone_refuses_with_evidence_and_mints_without_it()
    {
        InMemoryOsCredentialStore inner = new();

        CountingCredentialStore store = new(inner);

        using MemoryErasureKeyring withEvidence = new(store);

        byte[] original;

        using (MemoryErasureKey? created = withEvidence.OpenOrCreate(evidenceRowsExist: false).Key)
        {
            Assert.NotNull(created);

            original = created.KeyId.ToArray();
        }

        _ = inner.Delete(Service, Account);

        int writes = store.SetCount;

        MemoryErasureKeyOpenResult refused = withEvidence.OpenOrCreate(evidenceRowsExist: true);

        Assert.Equal(MemoryErasureKeyState.Absent, refused.State);

        Assert.Null(refused.Key);

        Assert.Equal(writes, store.SetCount);

        Assert.Equal(MemoryErasureKeyState.Absent, withEvidence.Latch.State);

        using MemoryErasureKeyring withoutEvidence = new(store);

        using (MemoryErasureKey? first = withoutEvidence.OpenOrCreate(evidenceRowsExist: false).Key)
        {
            Assert.NotNull(first);
        }

        Assert.Equal(writes + 1, store.SetCount);

        byte[] latched = withoutEvidence.Latch.KeyId!;

        _ = inner.Delete(Service, Account);

        MemoryErasureKeyOpenResult minted = withoutEvidence.OpenOrCreate(evidenceRowsExist: false);

        using (MemoryErasureKey? replacement = minted.Key)
        {
            Assert.Equal(MemoryErasureKeyState.Present, minted.State);

            Assert.NotNull(replacement);

            Assert.False(replacement.HasKeyId(latched));

            Assert.False(replacement.HasKeyId(original));
        }

        Assert.Equal(writes + 2, store.SetCount);
    }

    /// <summary>The key id computed from the labelled HMAC directly, independent of the grammar.</summary>
    private static byte[] IndependentKeyId(byte[] key) =>
        HMACSHA256.HashData(key, "Arcanum.MemoryErasure.KeyId.v1\0"u8)[..16];

    private static void AssertSingleSingleton<TService>(IServiceCollection services)
    {
        ServiceDescriptor descriptor = Assert.Single(
            services,
            static candidate => candidate.ServiceType == typeof(TService));

        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    /// <summary>
    /// Wraps an in-memory store, counting every call and the most calls ever in flight at once.
    /// </summary>
    private sealed class CountingCredentialStore(
        InMemoryOsCredentialStore inner,
        TimeSpan readDelay = default) : IOsCredentialStore, IOsCredentialPresenceProbe
    {
        private int _probeCount;

        private int _tryGetCount;

        private int _setCount;

        private int _deleteCount;

        private int _inFlight;

        private int _maxConcurrentCalls;

        public int TryGetCount => Volatile.Read(ref _tryGetCount);

        public int SetCount => Volatile.Read(ref _setCount);

        public int DeleteCount => Volatile.Read(ref _deleteCount);

        public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

        public int ProbeCount => Volatile.Read(ref _probeCount);

        public bool IsAvailable => true;

        public OsCredentialStoreStatus ProbePresence(string service, string account)
        {
            _ = Interlocked.Increment(ref _probeCount);

            return inner.ProbePresence(service, account);
        }

        public OsCredentialStoreResult TryGet(string service, string account)
        {
            _ = Interlocked.Increment(ref _tryGetCount);

            return Observe(() =>
            {
                if (readDelay > TimeSpan.Zero)
                {
                    Thread.Sleep(readDelay);
                }

                return inner.TryGet(service, account);
            });
        }

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {
            _ = Interlocked.Increment(ref _setCount);

            return Observe(() => inner.Set(service, account, secret));
        }

        public OsCredentialStoreResult Delete(string service, string account)
        {
            _ = Interlocked.Increment(ref _deleteCount);

            return Observe(() => inner.Delete(service, account));
        }

        private OsCredentialStoreResult Observe(Func<OsCredentialStoreResult> call)
        {
            int inFlight = Interlocked.Increment(ref _inFlight);

            int observed = Volatile.Read(ref _maxConcurrentCalls);

            while (inFlight > observed)
            {
                int previous = Interlocked.CompareExchange(ref _maxConcurrentCalls, inFlight, observed);

                if (previous == observed)
                {
                    break;
                }

                observed = previous;
            }

            try
            {
                return call();
            }
            finally
            {
                _ = Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>
    /// Holds every read inside the store until released, the way a keychain prompt holds a probe.
    /// </summary>
    private sealed class GatedCredentialStore(InMemoryOsCredentialStore inner) : IOsCredentialStore, IDisposable
    {
        private readonly SemaphoreSlim _held = new(0);

        private readonly ManualResetEventSlim _released = new(false);

        public bool IsAvailable => true;

        public Task<bool> WaitUntilHeldAsync() => _held.WaitAsync(TimeSpan.FromSeconds(30));

        public void Hold() => _released.Reset();

        public void Release() => _released.Set();

        public OsCredentialStoreResult TryGet(string service, string account)
        {
            _ = _held.Release();

            _ = _released.Wait(TimeSpan.FromSeconds(30));

            return inner.TryGet(service, account);
        }

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            inner.Set(service, account, secret);

        public OsCredentialStoreResult Delete(string service, string account) =>
            inner.Delete(service, account);

        // Releases rather than disposes: a read still parked on a failing run must be able to finish.
        public void Dispose() => _released.Set();
    }

    /// <summary>
    /// Answers reads from a script, in order, and accepts every write. An unscripted read or any
    /// delete fails the test.
    /// </summary>
    private sealed class ScriptedCredentialStore : IOsCredentialStore
    {
        private readonly Queue<Func<OsCredentialStoreResult>> _reads = new();

        private Func<string, OsCredentialStoreResult>? _write;

        public int TryGetCount { get; private set; }

        public int SetCount { get; private set; }

        public int DeleteCount { get; private set; }

        public bool IsAvailable => true;

        public ScriptedCredentialStore Read(OsCredentialStoreResult result)
        {
            _reads.Enqueue(() => result);

            return this;
        }

        public ScriptedCredentialStore ReadThrows(Exception exception)
        {
            _reads.Enqueue(() => throw exception);

            return this;
        }

        /// <summary>Answers every write with <paramref name="result"/> instead of echoing the secret.</summary>
        public ScriptedCredentialStore Write(OsCredentialStoreResult result)
        {
            _write = _ => result;

            return this;
        }

        public ScriptedCredentialStore WriteThrows(Exception exception)
        {
            _write = _ => throw exception;

            return this;
        }

        public OsCredentialStoreResult TryGet(string service, string account)
        {
            TryGetCount++;

            AssertIdentity(service, account);

            if (!_reads.TryDequeue(out Func<OsCredentialStoreResult>? next))
            {
                Assert.Fail("The keyring read the credential store more often than the script allows.");
            }

            return next();
        }

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {
            SetCount++;

            AssertIdentity(service, account);

            return _write is null ? OsCredentialStoreResult.Ok(secret) : _write(secret);
        }

        public OsCredentialStoreResult Delete(string service, string account)
        {
            DeleteCount++;

            Assert.Fail("The keyring must never delete the erasure key.");

            return OsCredentialStoreResult.NotFound();
        }

        private static void AssertIdentity(string service, string account)
        {
            Assert.Equal(Service, service);

            Assert.Equal(Account, account);
        }
    }

    /// <summary>A store whose presence probe throws, and whose every other member fails the test.</summary>
    private sealed class ThrowingProbeCredentialStore : IOsCredentialStore, IOsCredentialPresenceProbe
    {
        public bool IsAvailable => true;

        public OsCredentialStoreStatus ProbePresence(string service, string account) =>
            throw new IOException("test probe failure");

        public OsCredentialStoreResult TryGet(string service, string account) => Forbidden();

        public OsCredentialStoreResult Set(string service, string account, string secret) => Forbidden();

        public OsCredentialStoreResult Delete(string service, string account) => Forbidden();

        private static OsCredentialStoreResult Forbidden()
        {
            Assert.Fail("The presence probe must not read, write or delete the secret.");

            return OsCredentialStoreResult.NotFound();
        }
    }

    /// <summary>
    /// An in-memory store whose presence probe is held until released, the way a keychain prompt holds one.
    /// </summary>
    private sealed class GatedPresenceCredentialStore(InMemoryOsCredentialStore inner)
        : IOsCredentialStore, IOsCredentialPresenceProbe, IDisposable
    {
        private readonly SemaphoreSlim _held = new(0);

        private readonly ManualResetEventSlim _released = new(true);

        public bool IsAvailable => true;

        public Task<bool> WaitUntilHeldAsync() => _held.WaitAsync(TimeSpan.FromSeconds(30));

        public void Hold() => _released.Reset();

        public void Release() => _released.Set();

        public OsCredentialStoreStatus ProbePresence(string service, string account)
        {
            _ = _held.Release();

            _ = _released.Wait(TimeSpan.FromSeconds(30));

            return inner.ProbePresence(service, account);
        }

        public OsCredentialStoreResult TryGet(string service, string account) => inner.TryGet(service, account);

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            inner.Set(service, account, secret);

        public OsCredentialStoreResult Delete(string service, string account) => inner.Delete(service, account);

        // Releases rather than disposes: a probe still parked on a failing run must be able to finish.
        public void Dispose() => _released.Set();
    }

    /// <summary>
    /// An in-memory store whose presence probe can be told to answer a fault status or to throw, and which
    /// counts the secret reads so a test can show a probe read none.
    /// </summary>
    private sealed class FaultablePresenceCredentialStore(InMemoryOsCredentialStore inner)
        : IOsCredentialStore, IOsCredentialPresenceProbe
    {
        private int _tryGetCount;

        internal OsCredentialStoreStatus? Answer { get; set; }

        internal bool Throw { get; set; }

        public int TryGetCount => Volatile.Read(ref _tryGetCount);

        public bool IsAvailable => true;

        public OsCredentialStoreStatus ProbePresence(string service, string account) =>
            Throw
                ? throw new IOException("test probe failure")
                : Answer ?? inner.ProbePresence(service, account);

        public OsCredentialStoreResult TryGet(string service, string account)
        {
            _ = Interlocked.Increment(ref _tryGetCount);

            return inner.TryGet(service, account);
        }

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            inner.Set(service, account, secret);

        public OsCredentialStoreResult Delete(string service, string account) =>
            inner.Delete(service, account);
    }

    /// <summary>Fails the test from every member, and counts the attempts in case a caller swallows that.</summary>
    private sealed class ForbiddenCredentialStore : IOsCredentialStore
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool IsAvailable => Forbidden<bool>();

        public OsCredentialStoreResult TryGet(string service, string account) =>
            Forbidden<OsCredentialStoreResult>();

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            Forbidden<OsCredentialStoreResult>();

        public OsCredentialStoreResult Delete(string service, string account) =>
            Forbidden<OsCredentialStoreResult>();

        private T Forbidden<T>()
        {
            _ = Interlocked.Increment(ref _calls);

            Assert.Fail("This path must perform no credential I/O.");

            return default!;
        }
    }
}
