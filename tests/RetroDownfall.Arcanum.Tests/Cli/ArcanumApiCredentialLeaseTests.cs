using System.Net;
using System.Security.Cryptography;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ArcanumApiCredentialLeaseTests
{
    private const string ApiKey = "credential-lease-test-key";

    private static readonly Uri PresenceUri =
        new("http://localhost:5001/api/presence");

    [Fact]
    public async Task Valid_encrypted_mirror_avoids_the_os_store_and_is_cached_for_the_process()
    {
        PresenceHandler handler = new(ApiKey);

        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));

        RecordingReader operatingSystem = new(
            SecretStoreReadResult.Corrupted("must not be read"));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        ApiCredentialLeaseResult first = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        ApiCredentialLeaseResult second = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(first.IsVerified);
        Assert.NotNull(first.ProcessCapability);
        Assert.NotEqual(ApiKey, first.ProcessCapability);
        Assert.True(handler.IsValidCapability(first.ProcessCapability));
        Assert.Same(first, second);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
        Assert.Equal(0, handler.ApiKeyHeaderCount);
    }

    [Fact]
    public async Task Missing_mirror_falls_back_to_one_os_store_read()
    {
        PresenceHandler handler = new(ApiKey);

        RecordingReader mirror = new(SecretStoreReadResult.Missing());

        RecordingReader operatingSystem = new(
            SecretStoreReadResult.Ok(ApiKey));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        ApiCredentialLeaseResult result = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.NotNull(result.ProcessCapability);
        Assert.NotEqual(ApiKey, result.ProcessCapability);
        Assert.True(handler.IsValidCapability(result.ProcessCapability));
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(1, operatingSystem.ReadCount);
        Assert.Equal(0, handler.ApiKeyHeaderCount);
    }

    [Fact]
    public async Task Stale_mirror_falls_back_to_the_matching_os_credential_once()
    {
        PresenceHandler handler = new(ApiKey);
        RecordingReader mirror = new(SecretStoreReadResult.Ok("stale-mirror-key"));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Ok(ApiKey));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        ApiCredentialLeaseResult first = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        ApiCredentialLeaseResult second = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(first.IsVerified);
        Assert.NotNull(first.ProcessCapability);
        Assert.NotEqual(ApiKey, first.ProcessCapability);
        Assert.True(handler.IsValidCapability(first.ProcessCapability));
        Assert.Same(first, second);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(1, operatingSystem.ReadCount);
        Assert.Equal(0, handler.ApiKeyHeaderCount);
    }

    [Fact]
    public async Task Foreign_unauthorized_response_never_reads_or_sends_a_credential()
    {
        PresenceHandler handler = new(
            ApiKey,
            responseStatus: HttpStatusCode.Unauthorized);

        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));

        RecordingReader operatingSystem = new(SecretStoreReadResult.Ok(ApiKey));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        ApiCredentialLeaseResult result = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.False(result.IsVerified);
        Assert.Null(result.ProcessCapability);
        Assert.Equal(HealthProbeState.UnexpectedResponder, result.ProbeState);
        Assert.Equal(0, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
        Assert.Equal(0, handler.ApiKeyHeaderCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, HealthProbeState.UnhealthyStatus)]
    [InlineData(HttpStatusCode.OK, HealthProbeState.UnexpectedResponder)]
    public async Task Unusable_presence_response_never_opens_secure_storage(
        HttpStatusCode responseStatus,
        HealthProbeState expectedState)
    {
        PresenceHandler handler = new(ApiKey, responseStatus: responseStatus);
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Ok(ApiKey));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        ApiCredentialLeaseResult result = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.False(result.IsVerified);
        Assert.Equal(expectedState, result.ProbeState);
        Assert.Equal(0, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
        Assert.Equal(0, handler.ApiKeyHeaderCount);
    }

    [Fact]
    public async Task Duplicate_proof_header_is_rejected_before_secure_storage_is_opened()
    {
        PresenceHandler handler = new(ApiKey, duplicateProofHeader: true);
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Ok(ApiKey));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        ApiCredentialLeaseResult result = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.False(result.IsVerified);
        Assert.Equal(HealthProbeState.UnexpectedResponder, result.ProbeState);
        Assert.Equal(0, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Replayed_or_wrong_key_proof_never_releases_the_api_key()
    {
        PresenceHandler handler = new(
            ApiKey,
            proofKey: "different-server-key",
            replayProof: true);

        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));

        RecordingReader operatingSystem = new(SecretStoreReadResult.Ok(ApiKey));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        ApiCredentialLeaseResult result = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.False(result.IsVerified);
        Assert.Null(result.ProcessCapability);
        Assert.Equal(HealthProbeState.Unauthorized, result.ProbeState);
        Assert.Equal(SecretStoreReadStatus.Ok, result.CredentialStatus);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(1, operatingSystem.ReadCount);
        Assert.Equal(0, handler.ApiKeyHeaderCount);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_challenge_and_one_local_read()
    {
        PresenceHandler handler = new(ApiKey);

        RecordingReader mirror = new(
            SecretStoreReadResult.Ok(ApiKey),
            delay: TimeSpan.FromMilliseconds(20));

        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        ApiCredentialLeaseResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => lease.ResolveAsync(
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None)));

        Assert.All(results, static result => Assert.True(result.IsVerified));
        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Missing_or_corrupt_credentials_are_typed_and_cached_without_reprompting()
    {
        PresenceHandler handler = new(ApiKey);

        RecordingReader mirror = new(
            SecretStoreReadResult.Corrupted("mirror corrupt"));

        RecordingReader operatingSystem = new(
            SecretStoreReadResult.Corrupted("keychain denied"));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        ApiCredentialLeaseResult first = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        ApiCredentialLeaseResult second = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.False(first.IsVerified);
        Assert.Same(first, second);
        Assert.Null(first.ProcessCapability);
        Assert.Equal(SecretStoreReadStatus.Corrupted, first.CredentialStatus);
        Assert.DoesNotContain("keychain denied", first.Guidance, StringComparison.Ordinal);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(1, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Presence_timeout_does_not_cut_off_a_local_password_prompt()
    {
        PresenceHandler handler = new(ApiKey);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<SecretStoreReadResult> ReadMirror(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);

            return SecretStoreReadResult.Ok(ApiKey);
        }

        using ArcanumApiCredentialLease lease = new(
            staticClient: new HttpClient(handler),
            presenceUri: PresenceUri,
            mirrorReader: ReadMirror,
            primaryReader: static _ => Task.FromResult(SecretStoreReadResult.Missing()));

        Task<ApiCredentialLeaseResult> resolution = lease.ResolveAsync(
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.False(resolution.IsCompleted);

        release.TrySetResult();

        ApiCredentialLeaseResult result = await resolution;

        Assert.True(result.IsVerified);
    }

    [Fact]
    public async Task Caller_cancellation_during_shared_local_read_CancelsOnlyThatWaiter()
    {
        PresenceHandler handler = new(ApiKey);
        CancellableFirstReader mirror = new(ApiKey);
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        using ArcanumApiCredentialLease lease = new(
            staticClient: new HttpClient(handler),
            presenceUri: PresenceUri,
            mirrorReader: mirror.ReadAsync,
            primaryReader: operatingSystem.ReadAsync);

        using CancellationTokenSource cancellation = new();

        Task<ApiCredentialLeaseResult> first = lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            cancellation.Token);

        await mirror.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<ApiCredentialLeaseResult> second = lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        Assert.False(second.IsCompleted);
        Assert.Equal(1, handler.RequestCount);

        mirror.ReleaseFirstRead();

        ApiCredentialLeaseResult recovered = await second;

        Assert.True(recovered.IsVerified);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Sole_caller_cancellation_does_not_duplicate_a_non_cancellable_os_read()
    {
        PresenceHandler handler = new(ApiKey);
        NonCancellableFirstReader mirror = new(ApiKey);
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        using ArcanumApiCredentialLease lease = new(
            staticClient: new HttpClient(handler),
            presenceUri: PresenceUri,
            mirrorReader: mirror.ReadAsync,
            primaryReader: operatingSystem.ReadAsync);

        using CancellationTokenSource cancellation = new();

        Task<ApiCredentialLeaseResult> resolution = lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            cancellation.Token);

        await mirror.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolution);

        Task<ApiCredentialLeaseResult> retry = lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, operatingSystem.ReadCount);

        mirror.ReleaseFirstRead();

        ApiCredentialLeaseResult recovered = await retry;

        Assert.True(recovered.IsVerified);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Concurrent_initial_transient_callers_share_one_attempt_ThenALaterCallerRetries()
    {
        PresenceHandler handler = new(ApiKey);
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        handler.FailNext(HttpStatusCode.ServiceUnavailable);
        RequestHold hold = handler.HoldNextRequest();

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        Task<ApiCredentialLeaseResult>[] callers =
        [
            .. Enumerable.Range(0, 16)
                .Select(_ => lease.ResolveAsync(
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None)),
        ];

        await hold.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();
        hold.Release();

        ApiCredentialLeaseResult[] transient = await Task.WhenAll(callers);

        Assert.All(
            transient,
            static result => Assert.Equal(HealthProbeState.UnhealthyStatus, result.ProbeState));
        Assert.All(transient, result => Assert.Same(transient[0], result));
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);

        ApiCredentialLeaseResult recovered = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(recovered.IsVerified);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Disposing_the_lease_cancels_one_shared_attempt_for_all_waiters()
    {
        PresenceHandler handler = new(ApiKey);
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());
        RequestHold hold = handler.HoldNextRequest();

        ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        Task<ApiCredentialLeaseResult> first = lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        Task<ApiCredentialLeaseResult> second = lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        await hold.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        lease.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Disposal_wins_before_flight_publication_and_never_releases_a_capability()
    {
        PresenceHandler handler = new(ApiKey);
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());
        RequestHold requestHold = handler.HoldNextRequest();
        TaskCompletionSource publicationReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        using ManualResetEventSlim releasePublication = new(initialState: false);

        ArcanumApiCredentialLease lease = new(
            staticClient: new HttpClient(handler),
            presenceUri: PresenceUri,
            mirrorReader: mirror.ReadAsync,
            primaryReader: operatingSystem.ReadAsync,
            beforeFlightPublication: () =>
            {
                publicationReached.TrySetResult();
                Assert.True(releasePublication.Wait(TimeSpan.FromSeconds(2)));
            });

        Task<ApiCredentialLeaseResult> resolution = lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        await requestHold.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        requestHold.Release();
        await publicationReached.Task.WaitAsync(TimeSpan.FromSeconds(2));

        lease.Dispose();
        releasePublication.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolution);
    }

    [Fact]
    public async Task Concurrent_refresh_after_a_server_restart_is_single_flight_and_never_rereads_storage()
    {
        PresenceHandler handler = new(ApiKey);
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        ApiCredentialLeaseResult initial = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        string rejected = Assert.IsType<string>(initial.ProcessCapability);
        handler.Restart();

        ApiCredentialLeaseResult[] refreshed = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => lease.RefreshAsync(
                    rejected,
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None)));

        Assert.All(refreshed, static result => Assert.True(result.IsVerified));
        Assert.All(
            refreshed,
            result => Assert.Same(refreshed[0], result));
        Assert.NotEqual(rejected, refreshed[0].ProcessCapability);
        Assert.True(handler.IsValidCapability(refreshed[0].ProcessCapability));
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Exact_rejected_capability_invalidation_is_local_AndNextResolveIsSingleFlight()
    {
        PresenceHandler handler = new(ApiKey);
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        ApiCredentialLeaseResult initial = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        string rejected = Assert.IsType<string>(initial.ProcessCapability);
        RequestHold hold = handler.HoldNextRequest();

        Assert.True(lease.InvalidateRejectedCapability(rejected));
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);

        Task<ApiCredentialLeaseResult>[] callers =
        [
            .. Enumerable.Range(0, 16)
                .Select(_ => lease.ResolveAsync(
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None)),
        ];

        await hold.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();
        hold.Release();

        ApiCredentialLeaseResult[] renewed = await Task.WhenAll(callers);

        Assert.All(renewed, static result => Assert.True(result.IsVerified));
        Assert.All(renewed, result => Assert.Same(renewed[0], result));
        Assert.NotEqual(rejected, renewed[0].ProcessCapability);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Stale_rejected_capability_cannot_evict_a_newer_cached_capability()
    {
        PresenceHandler handler = new(ApiKey);
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        ApiCredentialLeaseResult initial = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        string rejected = Assert.IsType<string>(initial.ProcessCapability);
        handler.Restart();

        ApiCredentialLeaseResult refreshed = await lease.RefreshAsync(
            rejected,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.False(lease.InvalidateRejectedCapability(rejected));

        ApiCredentialLeaseResult cached = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Same(refreshed, cached);
        Assert.NotEqual(rejected, cached.ProcessCapability);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Stale_rejected_capability_cannot_reuse_a_newer_capability_after_its_elapsed_expiry()
    {
        DateTimeOffset now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        ManualTimeProvider time = new(now);
        PresenceHandler handler = new(
            ApiKey,
            timeProvider: time,
            capabilityLifetime: TimeSpan.FromMinutes(10));
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem,
            time);

        ApiCredentialLeaseResult initial = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        string staleRejected = Assert.IsType<string>(initial.ProcessCapability);
        handler.Restart();

        ApiCredentialLeaseResult replacement = await lease.RefreshAsync(
            staleRejected,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        time.AdvanceTimestamp(TimeSpan.FromMinutes(10));

        ApiCredentialLeaseResult renewed = await lease.RefreshAsync(
            staleRejected,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(renewed.IsVerified);
        Assert.NotSame(replacement, renewed);
        Assert.NotEqual(replacement.ProcessCapability, renewed.ProcessCapability);
        Assert.True(handler.IsValidCapability(renewed.ProcessCapability));
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Transient_restart_refresh_retries_with_the_retained_digest_without_rereading_storage()
    {
        PresenceHandler handler = new(ApiKey);
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        ApiCredentialLeaseResult initial = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        string rejected = Assert.IsType<string>(initial.ProcessCapability);
        handler.Restart();
        handler.FailNext(HttpStatusCode.ServiceUnavailable);

        ApiCredentialLeaseResult transient = await lease.RefreshAsync(
            rejected,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.False(transient.IsVerified);
        Assert.Equal(HealthProbeState.UnhealthyStatus, transient.ProbeState);

        ApiCredentialLeaseResult recovered = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(recovered.IsVerified);
        Assert.NotEqual(rejected, recovered.ProcessCapability);
        Assert.True(handler.IsValidCapability(recovered.ProcessCapability));
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Concurrent_transient_refresh_callers_share_one_attempt_ThenALaterCallerRetries()
    {
        PresenceHandler handler = new(ApiKey);
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        ApiCredentialLeaseResult initial = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        string rejected = Assert.IsType<string>(initial.ProcessCapability);

        handler.Restart();
        handler.FailNext(HttpStatusCode.ServiceUnavailable);
        RequestHold hold = handler.HoldNextRequest();

        Task<ApiCredentialLeaseResult>[] callers =
        [
            .. Enumerable.Range(0, 16)
                .Select(_ => lease.RefreshAsync(
                    rejected,
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None)),
        ];

        await hold.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();
        hold.Release();

        ApiCredentialLeaseResult[] transient = await Task.WhenAll(callers);

        Assert.All(
            transient,
            static result => Assert.Equal(HealthProbeState.UnhealthyStatus, result.ProbeState));
        Assert.All(transient, result => Assert.Same(transient[0], result));
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);

        ApiCredentialLeaseResult recovered = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(recovered.IsVerified);
        Assert.NotEqual(rejected, recovered.ProcessCapability);
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Retained_digest_mismatch_never_reopens_the_os_store_AndIsCached()
    {
        PresenceHandler handler = new(ApiKey);
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Ok(ApiKey));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        ApiCredentialLeaseResult initial = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        string rejected = Assert.IsType<string>(initial.ProcessCapability);

        handler.Restart();
        handler.SetProofKey("attacker-controlled-proof-key");

        ApiCredentialLeaseResult mismatch = await lease.RefreshAsync(
            rejected,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        ApiCredentialLeaseResult cached = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.False(mismatch.IsVerified);
        Assert.Equal(SecretStoreReadStatus.Ok, mismatch.CredentialStatus);
        Assert.Same(mismatch, cached);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(2, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Rotated_encrypted_mirror_can_replace_a_retained_digest_without_opening_the_os_store()
    {
        const string rotatedKey = "rotated-credential-lease-test-key";

        PresenceHandler handler = new(ApiKey);
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Corrupted("must not be read"));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        ApiCredentialLeaseResult initial = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        string rejected = Assert.IsType<string>(initial.ProcessCapability);

        mirror.Replace(SecretStoreReadResult.Ok(rotatedKey));
        handler.RotateKey(rotatedKey);

        ApiCredentialLeaseResult refreshed = await lease.RefreshAsync(
            rejected,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(refreshed.IsVerified);
        Assert.NotEqual(rejected, refreshed.ProcessCapability);
        Assert.True(handler.IsValidCapability(refreshed.ProcessCapability));
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(2, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Theory]
    [InlineData("not-a-retry-after")]
    [InlineData("9999999")]
    public async Task Presence_throttling_is_typed_and_honors_a_bounded_retry_window(
        string retryAfter)
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        PresenceHandler handler = new(ApiKey, timeProvider: time);
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        handler.FailNext(HttpStatusCode.TooManyRequests, retryAfter);

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem,
            time);

        ApiCredentialLeaseResult throttled = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        ApiCredentialLeaseResult coalesced = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.False(throttled.IsVerified);
        Assert.Equal(HealthProbeState.UnhealthyStatus, throttled.ProbeState);
        Assert.NotNull(throttled.RetryNotBefore);
        Assert.InRange(
            throttled.RetryNotBefore.Value,
            time.GetUtcNow(),
            time.GetUtcNow() + ArcanumApiCredentialLease.MaximumPresenceRetryAfter);
        Assert.Same(throttled, coalesced);
        Assert.Equal(1, handler.RequestCount);

        time.Advance(ArcanumApiCredentialLease.MaximumPresenceRetryAfter + TimeSpan.FromMilliseconds(1));

        ApiCredentialLeaseResult recovered = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(recovered.IsVerified);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Presence_throttling_honors_a_valid_http_date_retry_window()
    {
        DateTimeOffset now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset retryAt = now + TimeSpan.FromSeconds(1);
        ManualTimeProvider time = new(now);
        PresenceHandler handler = new(ApiKey, timeProvider: time);
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        handler.FailNext(
            HttpStatusCode.TooManyRequests,
            retryAt.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem,
            time);

        ApiCredentialLeaseResult throttled = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Equal(HealthProbeState.UnhealthyStatus, throttled.ProbeState);
        Assert.Equal(retryAt, throttled.RetryNotBefore);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Capability_is_renewed_once_before_expiry_without_rereading_secure_storage()
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));

        PresenceHandler handler = new(
            ApiKey,
            timeProvider: time,
            capabilityLifetime: TimeSpan.FromMinutes(10));

        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem,
            time);

        ApiCredentialLeaseResult initial = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(8) + TimeSpan.FromSeconds(59));

        ApiCredentialLeaseResult stillFresh = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Same(initial, stillFresh);
        Assert.Equal(1, handler.RequestCount);

        time.Advance(TimeSpan.FromSeconds(1));

        ApiCredentialLeaseResult[] renewed = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => lease.ResolveAsync(
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None)));

        Assert.All(renewed, static result => Assert.True(result.IsVerified));
        Assert.All(renewed, result => Assert.Same(renewed[0], result));
        Assert.NotEqual(initial.ProcessCapability, renewed[0].ProcessCapability);
        Assert.True(handler.IsValidCapability(renewed[0].ProcessCapability));
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Proactive_transient_renewal_keeps_the_still_valid_capability()
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        PresenceHandler handler = new(
            ApiKey,
            timeProvider: time,
            capabilityLifetime: TimeSpan.FromMinutes(10));
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem,
            time);

        ApiCredentialLeaseResult initial = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(9));
        handler.FailNext(HttpStatusCode.ServiceUnavailable);

        ApiCredentialLeaseResult fallback = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Same(initial, fallback);
        Assert.Equal(2, handler.RequestCount);

        ApiCredentialLeaseResult renewed = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(renewed.IsVerified);
        Assert.NotEqual(initial.ProcessCapability, renewed.ProcessCapability);
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Proactive_throttled_renewal_keeps_the_capability_and_uses_monotonic_backoff()
    {
        DateTimeOffset now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        ManualTimeProvider time = new(now);
        PresenceHandler handler = new(
            ApiKey,
            timeProvider: time,
            capabilityLifetime: TimeSpan.FromMinutes(10));
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem,
            time);

        ApiCredentialLeaseResult initial = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(9));
        handler.FailNext(HttpStatusCode.TooManyRequests, "2");

        ApiCredentialLeaseResult fallback = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        time.SetUtcNow(now - TimeSpan.FromDays(1));

        ApiCredentialLeaseResult afterRollback = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        time.SetUtcNow(now + TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(30));

        ApiCredentialLeaseResult afterAdvance = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Same(initial, fallback);
        Assert.Same(initial, afterRollback);
        Assert.Same(initial, afterAdvance);
        Assert.Equal(2, handler.RequestCount);

        time.AdvanceTimestamp(TimeSpan.FromSeconds(2) + TimeSpan.FromMilliseconds(1));

        ApiCredentialLeaseResult renewed = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(renewed.IsVerified);
        Assert.NotEqual(initial.ProcessCapability, renewed.ProcessCapability);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task Proactive_throttle_never_serves_a_capability_after_its_signed_expiry()
    {
        DateTimeOffset now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        ManualTimeProvider time = new(now);
        PresenceHandler handler = new(
            ApiKey,
            timeProvider: time,
            capabilityLifetime: TimeSpan.FromMinutes(10));
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem,
            time);

        ApiCredentialLeaseResult initial = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        handler.FailNext(HttpStatusCode.TooManyRequests, "2");

        ApiCredentialLeaseResult fallback = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Same(initial, fallback);

        time.AdvanceTimestamp(TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(1));

        ApiCredentialLeaseResult expired = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.False(expired.IsVerified);
        Assert.Equal(HealthProbeState.UnhealthyStatus, expired.ProbeState);
        Assert.NotNull(expired.RetryNotBefore);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Forward_wall_clock_jump_does_not_prematurely_renew_a_cached_capability()
    {
        DateTimeOffset now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        ManualTimeProvider time = new(now);
        PresenceHandler handler = new(
            ApiKey,
            timeProvider: time,
            capabilityLifetime: TimeSpan.FromMinutes(10));
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem,
            time);

        ApiCredentialLeaseResult initial = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        time.SetUtcNow(now + TimeSpan.FromDays(30));

        ApiCredentialLeaseResult cached = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Same(initial, cached);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Backward_wall_clock_jump_cannot_extend_a_cached_capability_past_elapsed_expiry()
    {
        DateTimeOffset now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        ManualTimeProvider time = new(now);
        PresenceHandler handler = new(
            ApiKey,
            timeProvider: time,
            capabilityLifetime: TimeSpan.FromMinutes(10));
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem,
            time);

        ApiCredentialLeaseResult initial = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        time.AdvanceTimestamp(TimeSpan.FromMinutes(10));
        time.SetUtcNow(now - TimeSpan.FromDays(30));

        ApiCredentialLeaseResult renewed = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(renewed.IsVerified);
        Assert.NotSame(initial, renewed);
        Assert.NotEqual(initial.ProcessCapability, renewed.ProcessCapability);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Retry_after_date_accepts_only_the_exact_http_date_format()
    {
        DateTimeOffset now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        ManualTimeProvider time = new(now);
        PresenceHandler handler = new(ApiKey, timeProvider: time);
        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        handler.FailNext(
            HttpStatusCode.TooManyRequests,
            (now + TimeSpan.FromSeconds(1)).ToString("O"));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem,
            time);

        ApiCredentialLeaseResult throttled = await lease.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Equal(now + TimeSpan.FromMilliseconds(100), throttled.RetryNotBefore);
    }

    private static ArcanumApiCredentialLease CreateLease(
        PresenceHandler handler,
        RecordingReader mirror,
        RecordingReader operatingSystem,
        TimeProvider? timeProvider = null)
    {
        HttpClient client = new(handler)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };

        return new ArcanumApiCredentialLease(
            staticClient: client,
            presenceUri: PresenceUri,
            mirrorReader: mirror.ReadAsync,
            primaryReader: operatingSystem.ReadAsync,
            timeProvider: timeProvider);
    }

    private sealed class RecordingReader(
        SecretStoreReadResult result,
        TimeSpan? delay = null)
    {
        private int _readCount;

        private SecretStoreReadResult _result = result;

        internal int ReadCount => Volatile.Read(ref _readCount);

        internal async Task<SecretStoreReadResult> ReadAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCount);

            if (delay is { } wait)
            {
                await Task.Delay(wait, cancellationToken);
            }

            return _result;
        }

        internal void Replace(SecretStoreReadResult replacement) =>
            _result = replacement;
    }

    private sealed class CancellableFirstReader(string apiKey)
    {
        private int _readCount;

        internal TaskCompletionSource FirstReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ReadCount => Volatile.Read(ref _readCount);

        private TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ReleaseFirstRead() => Release.TrySetResult();

        internal async Task<SecretStoreReadResult> ReadAsync(
            CancellationToken cancellationToken)
        {
            int read = Interlocked.Increment(ref _readCount);

            if (read == 1)
            {
                FirstReadStarted.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return SecretStoreReadResult.Ok(apiKey);
        }
    }

    private sealed class NonCancellableFirstReader(string apiKey)
    {
        private int _readCount;

        internal TaskCompletionSource FirstReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ReadCount => Volatile.Read(ref _readCount);

        private TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ReleaseFirstRead() => Release.TrySetResult();

        internal async Task<SecretStoreReadResult> ReadAsync(
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Interlocked.Increment(ref _readCount);
            FirstReadStarted.TrySetResult();
            await Release.Task;

            return SecretStoreReadResult.Ok(apiKey);
        }
    }

    private sealed class PresenceHandler(
        string expectedKey,
        HttpStatusCode responseStatus = HttpStatusCode.NoContent,
        string? proofKey = null,
        bool replayProof = false,
        bool duplicateProofHeader = false,
        TimeProvider? timeProvider = null,
        TimeSpan? capabilityLifetime = null) : HttpMessageHandler
    {
        private string _expectedKey = expectedKey;

        private string? _proofKey = proofKey;

        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

        private readonly TimeSpan _capabilityLifetime =
            capabilityLifetime ?? ArcanumProcessCapabilityService.DefaultLifetime;

        private ArcanumProcessCapabilityService _processCapabilities =
            CreateCapabilityService(
                timeProvider ?? TimeProvider.System,
                capabilityLifetime ?? ArcanumProcessCapabilityService.DefaultLifetime);

        private int _requestCount;

        private int _apiKeyHeaderCount;

        private int _nextResponseStatus;

        private string? _nextRetryAfter;

        private RequestHold? _nextRequestHold;

        internal int RequestCount => Volatile.Read(ref _requestCount);

        internal int ApiKeyHeaderCount => Volatile.Read(ref _apiKeyHeaderCount);

        internal bool IsValidCapability(string? capability) =>
            _processCapabilities.IsValidEncoded(capability);

        internal void Restart()
        {
            ArcanumProcessCapabilityService previous = _processCapabilities;
            _processCapabilities = CreateCapabilityService(
                _timeProvider,
                _capabilityLifetime);
            previous.Dispose();
        }

        internal void RotateKey(string key)
        {
            _expectedKey = key;
            _proofKey = null;
            Restart();
        }

        internal void SetProofKey(string key) => _proofKey = key;

        internal void FailNext(HttpStatusCode status, string? retryAfter = null)
        {
            if (status == HttpStatusCode.NoContent)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(status),
                    "A one-shot failure must not be the successful presence status.");
            }

            Volatile.Write(ref _nextResponseStatus, (int)status);
            Volatile.Write(ref _nextRetryAfter, retryAfter);
        }

        internal RequestHold HoldNextRequest()
        {
            RequestHold hold = new();

            if (Interlocked.CompareExchange(ref _nextRequestHold, hold, null) is not null)
            {
                throw new InvalidOperationException("A request hold is already armed.");
            }

            return hold;
        }

        private static ArcanumProcessCapabilityService CreateCapabilityService(
            TimeProvider timeProvider,
            TimeSpan lifetime) =>
            new(
                timeProvider,
                RandomNumberGenerator.GetBytes(32),
                lifetime);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);

            RequestHold? hold = Interlocked.Exchange(ref _nextRequestHold, null);

            if (hold is not null)
            {
                hold.Started.TrySetResult();
                await hold.Released.Task.WaitAsync(cancellationToken);
            }

            if (request.Headers.Contains(ArcanumApiHeaders.ApiKey))
            {
                Interlocked.Increment(ref _apiKeyHeaderCount);
            }

            HttpStatusCode effectiveStatus = (HttpStatusCode)Interlocked.Exchange(
                ref _nextResponseStatus,
                0);

            if (effectiveStatus == 0)
            {
                effectiveStatus = responseStatus;
            }

            HttpResponseMessage response = new(effectiveStatus);

            if (effectiveStatus != HttpStatusCode.NoContent)
            {
                string? retryAfter = Interlocked.Exchange(ref _nextRetryAfter, null);

                if (retryAfter is not null)
                {
                    _ = response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
                }

                return response;
            }

            string encodedNonce = Assert.Single(
                request.Headers.GetValues(ArcanumApiHeaders.PresenceNonce));

            Assert.True(
                ArcanumPresenceProofProtocol.TryDecode(
                    encodedNonce,
                    ArcanumPresenceProofProtocol.NonceBytes,
                    out byte[]? nonce));

            if (replayProof)
            {
                CryptographicOperations.ZeroMemory(nonce!);
                nonce = RandomNumberGenerator.GetBytes(
                    ArcanumPresenceProofProtocol.NonceBytes);
            }

            Assert.True(
                ArcanumPresenceProofProtocol.TryCanonicalAuthority(
                    request.RequestUri!,
                    out string? authority));

            byte[] encodedKey = System.Text.Encoding.UTF8.GetBytes(
                _proofKey ?? _expectedKey);
            byte[] digest = SHA256.HashData(encodedKey);
            byte[] processCapability = _processCapabilities.Issue();
            byte[] capabilityEnvelope =
                ArcanumPresenceProofProtocol.CreateCapabilityEnvelope(
                    digest,
                    nonce!,
                    authority!,
                    processCapability);

            byte[] proof = ArcanumPresenceProofProtocol.ComputeProof(
                digest,
                nonce!,
                authority!,
                capabilityEnvelope);

            response.Headers.TryAddWithoutValidation(
                ArcanumApiHeaders.PresenceVersion,
                ArcanumPresenceProofProtocol.Version);

            response.Headers.TryAddWithoutValidation(
                ArcanumApiHeaders.PresenceAuthority,
                authority);

            response.Headers.TryAddWithoutValidation(
                ArcanumApiHeaders.PresenceProof,
                ArcanumPresenceProofProtocol.Encode(proof));

            response.Headers.TryAddWithoutValidation(
                ArcanumApiHeaders.PresenceCapability,
                ArcanumPresenceProofProtocol.Encode(capabilityEnvelope));

            if (duplicateProofHeader)
            {
                response.Headers.TryAddWithoutValidation(
                    ArcanumApiHeaders.PresenceProof,
                    ArcanumPresenceProofProtocol.Encode(proof));
            }

            CryptographicOperations.ZeroMemory(nonce!);
            CryptographicOperations.ZeroMemory(encodedKey);
            CryptographicOperations.ZeroMemory(digest);
            CryptographicOperations.ZeroMemory(processCapability);
            CryptographicOperations.ZeroMemory(capabilityEnvelope);
            CryptographicOperations.ZeroMemory(proof);

            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _processCapabilities.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class RequestHold
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Released { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => Released.TrySetResult();
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan duration)
        {
            _utcNow += duration;
            AdvanceTimestamp(duration);
        }

        internal void AdvanceTimestamp(TimeSpan duration) =>
            _timestamp += duration.Ticks;

        internal void SetUtcNow(DateTimeOffset value) => _utcNow = value;
    }
}
