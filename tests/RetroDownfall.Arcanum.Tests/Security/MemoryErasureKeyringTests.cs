using System.Buffers.Text;

using System.Security.Cryptography;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The erasure key lives in the OS credential store, one latched keyring per process.
/// </summary>
/// <remarks>
/// Automatic callers read the latch and never write; operator calls re-probe anything short of
/// <see cref="MemoryErasureKeyState.Present"/>; creation happens once, under the keyring's lock, and
/// only after a proven absence with no evidence rows. Every store here is a fake over
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

        MemoryErasureKeyring keyring = new(store);

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
        TimeSpan readDelay = default) : IOsCredentialStore
    {
        private int _tryGetCount;

        private int _setCount;

        private int _deleteCount;

        private int _inFlight;

        private int _maxConcurrentCalls;

        public int TryGetCount => Volatile.Read(ref _tryGetCount);

        public int SetCount => Volatile.Read(ref _setCount);

        public int DeleteCount => Volatile.Read(ref _deleteCount);

        public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

        public bool IsAvailable => true;

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

            return OsCredentialStoreResult.Ok(secret);
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
