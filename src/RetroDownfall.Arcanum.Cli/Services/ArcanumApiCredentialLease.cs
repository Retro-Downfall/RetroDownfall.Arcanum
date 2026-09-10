using System.Net;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Cli.Services;

/// <summary>
/// Holds one short-lived credential issued by the verified server process. The reusable master key
/// is read only during the first challenge, reduced to a fixed-size digest, and never sent over
/// HTTP. A server restart renews the process capability with that private digest and does not open
/// secure storage again.
/// </summary>
public sealed class ArcanumApiCredentialLease : IDisposable
{
    internal static readonly TimeSpan MaximumPresenceRetryAfter = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan MinimumPresenceRetryAfter = TimeSpan.FromMilliseconds(100);

    private const string MissingCredentialGuidance =
        "No local API credential was found. Run `arcanum serve` once to create it.";

    private const string UnreadableCredentialGuidance =
        "The local API credential could not be read. Run `arcanum doctor` before retrying.";

    private const string CredentialMismatchGuidance =
        "The server did not prove it owns this installation's credential. The API key was not sent; run `arcanum doctor`.";

    private const string UnexpectedResponderGuidance =
        "Something answered at Arcanum's local address but did not provide a valid Arcanum presence proof. The API key was not read or sent.";

    private readonly HttpClient _client;

    private readonly Uri _presenceUri;

    private readonly string _canonicalAuthority;

    private readonly Func<CancellationToken, Task<SecretStoreReadResult>> _mirrorReader;

    private readonly Func<CancellationToken, Task<SecretStoreReadResult>> _primaryReader;

    private readonly TimeProvider _timeProvider;

    private readonly Action? _beforeFlightPublication;
    private readonly object _flightSync = new();
    private readonly object _digestSync = new();

    private CachedLeaseState? _cached;

    private ResolutionFlight? _inFlight;

    private byte[]? _keyDigest;

    private int _disposed;

    public ArcanumApiCredentialLease(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<ArcanumSettings> settingsMonitor,
        DataProtectionSecretStore mirrorStore,
        ISecretStore primaryStore)
        : this(
            CreateHttpClient(httpClientFactory),
            CreatePresenceUri(settingsMonitor),
            CreateMirrorReader(mirrorStore),
            CreatePrimaryReader(primaryStore),
            TimeProvider.System)
    {
    }

    internal ArcanumApiCredentialLease(
        HttpClient staticClient,
        Uri presenceUri,
        Func<CancellationToken, Task<SecretStoreReadResult>> mirrorReader,
        Func<CancellationToken, Task<SecretStoreReadResult>> primaryReader,
        TimeProvider? timeProvider = null,
        Action? beforeFlightPublication = null)
    {
        ArgumentNullException.ThrowIfNull(staticClient);
        ArgumentNullException.ThrowIfNull(presenceUri);
        ArgumentNullException.ThrowIfNull(mirrorReader);
        ArgumentNullException.ThrowIfNull(primaryReader);

        if (!ArcanumPresenceProofProtocol.TryCanonicalAuthority(
                presenceUri,
                out string? canonicalAuthority))
        {
            throw new ArgumentException(
                "The presence URI must use an absolute loopback HTTP or HTTPS address.",
                nameof(presenceUri));
        }

        _client = staticClient;
        _presenceUri = presenceUri;
        _canonicalAuthority = canonicalAuthority!;
        _mirrorReader = mirrorReader;
        _primaryReader = primaryReader;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _beforeFlightPublication = beforeFlightPublication;
    }

    internal string CanonicalAuthority => _canonicalAuthority;

    internal async Task<ApiCredentialLeaseResult> ResolveAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();

        ApiCredentialLeaseResult? immediate;
        FlightWait? wait;

        lock (_flightSync)
        {
            ThrowIfDisposed();

            CachedLeaseState? cached = ReadUsableCachedStateNoLock();

            long nowTimestamp = _timeProvider.GetTimestamp();
            bool retryDelayActive = cached is not null
                && IsRetryDelayActiveNoLock(cached, nowTimestamp);

            if (cached is { Result.IsVerified: true }
                && cached.IsCapabilityUnexpired(_timeProvider, nowTimestamp)
                && (cached.IsBeforeRenewal(_timeProvider, nowTimestamp)
                    || retryDelayActive))
            {
                immediate = cached.Result;
                wait = null;
            }
            else if (cached is { Result.IsVerified: true, RetryFailure: not null }
                && retryDelayActive)
            {
                immediate = cached.RetryFailure;
                wait = null;
            }
            else if (cached is { Result.IsVerified: false })
            {
                immediate = cached.Result;
                wait = null;
            }
            else
            {
                immediate = null;
                wait = JoinOrCreateFlightNoLock(
                    timeout,
                    cached?.Result,
                    isProactiveRenewal: cached?.Result.IsVerified == true);
            }
        }

        if (immediate is not null)
        {
            return immediate;
        }

        return await AwaitFlightAsync(wait!, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<ApiCredentialLeaseResult> RefreshAsync(
        string rejectedCapability,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectedCapability);
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();

        ApiCredentialLeaseResult? immediate;
        FlightWait? wait;

        lock (_flightSync)
        {
            ThrowIfDisposed();

            CachedLeaseState? cached = ReadUsableCachedStateNoLock();
            long nowTimestamp = _timeProvider.GetTimestamp();

            if (cached is { Result.IsVerified: true, Result.ProcessCapability: not null }
                && cached.IsCapabilityUnexpired(_timeProvider, nowTimestamp)
                && !string.Equals(
                    cached.Result.ProcessCapability,
                    rejectedCapability,
                    StringComparison.Ordinal))
            {
                immediate = cached.Result;
                wait = null;
            }
            else if (cached is { Result.IsVerified: false })
            {
                immediate = cached.Result;
                wait = null;
            }
            else
            {
                immediate = null;

                if (cached is { Result.IsVerified: true }
                    && string.Equals(
                        cached.Result.ProcessCapability,
                        rejectedCapability,
                        StringComparison.Ordinal))
                {
                    _cached = null;
                }

                if (_inFlight is not null)
                {
                    _inFlight.CachedCapabilityWasRejected = true;
                }

                wait = JoinOrCreateFlightNoLock(
                    timeout,
                    fallback: null,
                    isProactiveRenewal: false);
            }
        }

        if (immediate is not null)
        {
            return immediate;
        }

        return await AwaitFlightAsync(wait!, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a capability only when it is still the exact cached value rejected by the server.
    /// This operation performs no presence request and never opens credential storage.
    /// </summary>
    internal bool InvalidateRejectedCapability(string rejectedCapability)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectedCapability);

        lock (_flightSync)
        {
            ThrowIfDisposed();

            CachedLeaseState? cached = ReadUsableCachedStateNoLock();

            if (cached is not { Result.IsVerified: true, Result.ProcessCapability: not null }
                || !string.Equals(
                    cached.Result.ProcessCapability,
                    rejectedCapability,
                    StringComparison.Ordinal))
            {
                return false;
            }

            _cached = null;

            if (_inFlight is not null)
            {
                _inFlight.CachedCapabilityWasRejected = true;
            }

            return true;
        }
    }

    public void Dispose()
    {
        ResolutionFlight? flight;

        lock (_flightSync)
        {
            if (_disposed != 0)
            {
                return;
            }

            Volatile.Write(ref _disposed, 1);
            _cached = null;
            flight = _inFlight;
            _inFlight = null;

            flight?.Completion.TrySetCanceled();
        }

        if (flight is not null)
        {
            CancelFlight(flight);
        }

        byte[]? digest;

        lock (_digestSync)
        {
            digest = _keyDigest;
            _keyDigest = null;
        }

        if (digest is not null)
        {
            CryptographicOperations.ZeroMemory(digest);
        }

        _client.Dispose();
    }

    private async Task<ApiCredentialLeaseResult> AwaitFlightAsync(
        FlightWait wait,
        CancellationToken cancellationToken)
    {
        if (wait.StartsFlight)
        {
            _ = CompleteSingleFlightAsync(wait.Flight);
        }

        try
        {
            ApiCredentialLeaseResult result = await wait.Flight.Completion.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (wait.Fallback is not null
                && !result.IsVerified
                && result.CredentialStatus is null)
            {
                lock (_flightSync)
                {
                    if (CanUseFallbackNoLock(wait.Fallback))
                    {
                        return wait.Fallback;
                    }
                }
            }

            return result;
        }
        finally
        {
            ReleaseWaiter(wait.Flight);
        }
    }

    private FlightWait JoinOrCreateFlightNoLock(
        TimeSpan timeout,
        ApiCredentialLeaseResult? fallback,
        bool isProactiveRenewal)
    {
        bool startsFlight = false;

        if (_inFlight is null)
        {
            _inFlight = new ResolutionFlight(timeout, isProactiveRenewal);
            startsFlight = true;
        }

        _inFlight.WaiterCount++;

        return new FlightWait(_inFlight, fallback, startsFlight);
    }

    private void ReleaseWaiter(ResolutionFlight flight)
    {
        lock (_flightSync)
        {
            if (flight.WaiterCount <= 0)
            {
                return;
            }

            flight.WaiterCount--;
        }
    }

    private static void CancelFlight(ResolutionFlight flight)
    {
        try
        {
            flight.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion won the race and no cancellable work remains.
        }
    }

    private async Task CompleteSingleFlightAsync(
        ResolutionFlight flight)
    {
        try
        {
            using CredentialResolution resolution = await ResolveUncachedAsync(
                    flight.Timeout,
                    flight.Cancellation.Token)
                .ConfigureAwait(false);

            ApiCredentialLeaseResult result = resolution.Result;

            _beforeFlightPublication?.Invoke();

            lock (_flightSync)
            {
                if (_disposed != 0
                    || !ReferenceEquals(_inFlight, flight))
                {
                    flight.Completion.TrySetCanceled();

                    return;
                }

                byte[]? replacementDigest = resolution.TakeDigest();

                if (replacementDigest is not null)
                {
                    CacheDigestAndZero(replacementDigest);
                }

                PublishResultNoLock(flight, result);
                _inFlight = null;
                flight.Completion.TrySetResult(result);
            }
        }
        catch (OperationCanceledException) when (flight.Cancellation.IsCancellationRequested)
        {
            lock (_flightSync)
            {
                if (ReferenceEquals(_inFlight, flight))
                {
                    _inFlight = null;
                }

                flight.Completion.TrySetCanceled();
            }
        }
        catch (Exception exception)
        {
            lock (_flightSync)
            {
                if (ReferenceEquals(_inFlight, flight))
                {
                    _inFlight = null;
                }

                flight.Completion.TrySetException(exception);
            }
        }
        finally
        {
            flight.Cancellation.Dispose();
        }
    }

    private CachedLeaseState? ReadUsableCachedStateNoLock()
    {
        CachedLeaseState? cached = _cached;

        long nowTimestamp = _timeProvider.GetTimestamp();

        if (cached?.RetryDelay is null
            || IsRetryDelayActiveNoLock(cached, nowTimestamp))
        {
            return cached;
        }

        cached.ClearRetryDelay();

        if (!cached.Result.IsVerified)
        {
            _cached = null;

            return null;
        }

        return cached;
    }

    private bool IsRetryDelayActiveNoLock(
        CachedLeaseState cached,
        long nowTimestamp) =>
        cached.RetryDelay is { } retryDelay
        && _timeProvider.GetElapsedTime(
            cached.RetryStartedTimestamp,
            nowTimestamp) is { } elapsed
        && elapsed >= TimeSpan.Zero
        && elapsed < retryDelay;

    private bool CanUseFallbackNoLock(ApiCredentialLeaseResult fallback) =>
        _cached is { Result.IsVerified: true } cached
        && ReferenceEquals(cached.Result, fallback)
        && cached.IsCapabilityUnexpired(
            _timeProvider,
            _timeProvider.GetTimestamp());

    private void PublishResultNoLock(
        ResolutionFlight flight,
        ApiCredentialLeaseResult result)
    {
        if (result.IsVerified || result.CredentialStatus is not null)
        {
            _cached = CachedLeaseState.Create(result, _timeProvider);

            return;
        }

        if (flight.IsProactiveRenewal
            && !flight.CachedCapabilityWasRejected
            && _cached is { Result.IsVerified: true } cached
            && cached.IsCapabilityUnexpired(
                _timeProvider,
                _timeProvider.GetTimestamp()))
        {
            if (result.RetryDelay is { } retryDelay)
            {
                cached.SetRetryDelay(
                    _timeProvider.GetTimestamp(),
                    retryDelay,
                    result);
            }

            return;
        }

        if (result.RetryDelay is not null)
        {
            _cached = CachedLeaseState.Create(result, _timeProvider);
        }
    }

    private async Task<CredentialResolution> ResolveInitialAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(ArcanumPresenceProofProtocol.NonceBytes);

        try
        {
            PresenceChallengeResult challenge = await RequestChallengeAsync(
                    nonce,
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!challenge.IsUsable)
            {
                return CredentialResolution.WithoutDigest(challenge.Failure!);
            }

            using (challenge)
            {
                SecretStoreReadResult mirror = await ReadCredentialAsync(
                        _mirrorReader,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (TryUnlockCapability(
                        mirror,
                        nonce,
                        challenge,
                        out string? mirrorCapability,
                        out DateTimeOffset mirrorRenewAfter,
                        out DateTimeOffset mirrorExpiresAt,
                        out ApiCredentialCapabilityWindow mirrorWindow,
                        out byte[]? mirrorDigest))
                {
                    return CredentialResolution.WithDigest(
                        ApiCredentialLeaseResult.Verified(
                            mirrorCapability!,
                            mirrorRenewAfter,
                            mirrorExpiresAt,
                            mirrorWindow),
                        mirrorDigest!);
                }

                SecretStoreReadResult primary = await ReadCredentialAsync(
                        _primaryReader,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (TryUnlockCapability(
                        primary,
                        nonce,
                        challenge,
                        out string? primaryCapability,
                        out DateTimeOffset primaryRenewAfter,
                        out DateTimeOffset primaryExpiresAt,
                        out ApiCredentialCapabilityWindow primaryWindow,
                        out byte[]? primaryDigest))
                {
                    return CredentialResolution.WithDigest(
                        ApiCredentialLeaseResult.Verified(
                            primaryCapability!,
                            primaryRenewAfter,
                            primaryExpiresAt,
                            primaryWindow),
                        primaryDigest!);
                }

                SecretStoreReadStatus status = CombineCredentialStatus(mirror, primary);

                return CredentialResolution.WithoutDigest(
                    ApiCredentialLeaseResult.CredentialFailure(
                        status,
                        status == SecretStoreReadStatus.Ok
                            ? CredentialMismatchGuidance
                            : status == SecretStoreReadStatus.Corrupted
                                ? UnreadableCredentialGuidance
                                : MissingCredentialGuidance));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private async Task<CredentialResolution> ResolveUncachedAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        byte[]? retainedDigest = CopyDigest();

        if (retainedDigest is null)
        {
            return await ResolveInitialAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            return await RefreshCoreAsync(
                    retainedDigest,
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(retainedDigest);
        }
    }

    private async Task<CredentialResolution> RefreshCoreAsync(
        byte[] keyDigest,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(ArcanumPresenceProofProtocol.NonceBytes);

        try
        {
            PresenceChallengeResult challenge = await RequestChallengeAsync(
                    nonce,
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!challenge.IsUsable)
            {
                return CredentialResolution.WithoutDigest(challenge.Failure!);
            }

            using (challenge)
            {
                if (TryDecryptVerifiedCapability(
                        keyDigest,
                        nonce,
                        challenge,
                        out string? processCapability,
                        out DateTimeOffset renewAfter,
                        out DateTimeOffset expiresAt,
                        out ApiCredentialCapabilityWindow capabilityWindow))
                {
                    return CredentialResolution.WithoutDigest(
                        ApiCredentialLeaseResult.Verified(
                            processCapability!,
                            renewAfter,
                            expiresAt,
                            capabilityWindow));
                }

                SecretStoreReadResult mirror = await ReadCredentialAsync(
                        _mirrorReader,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (TryUnlockCapability(
                        mirror,
                        nonce,
                        challenge,
                        out string? mirrorCapability,
                        out DateTimeOffset mirrorRenewAfter,
                        out DateTimeOffset mirrorExpiresAt,
                        out ApiCredentialCapabilityWindow mirrorWindow,
                        out byte[]? mirrorDigest))
                {
                    bool rotated = !CryptographicOperations.FixedTimeEquals(
                        keyDigest,
                        mirrorDigest!);

                    if (rotated)
                    {
                        return CredentialResolution.WithDigest(
                            ApiCredentialLeaseResult.Verified(
                                mirrorCapability!,
                                mirrorRenewAfter,
                                mirrorExpiresAt,
                                mirrorWindow),
                            mirrorDigest!);
                    }

                    CryptographicOperations.ZeroMemory(mirrorDigest!);
                }

                return CredentialResolution.WithoutDigest(
                    ApiCredentialLeaseResult.CredentialFailure(
                        SecretStoreReadStatus.Ok,
                        CredentialMismatchGuidance));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private async Task<PresenceChallengeResult> RequestChallengeAsync(
        byte[] nonce,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, _presenceUri);
        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeoutSource.CancelAfter(timeout);

        _ = request.Headers.TryAddWithoutValidation(
            ArcanumApiHeaders.PresenceNonce,
            ArcanumPresenceProofProtocol.Encode(nonce));

        long requestStartedTimestamp = _timeProvider.GetTimestamp();

        try
        {
            using HttpResponseMessage response = await _client
                .SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutSource.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return PresenceChallengeResult.Failed(
                    HealthProbeState.UnhealthyStatus,
                    "Arcanum is starting but its credential proof is not ready yet.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                PresenceRetryWindow retryWindow = ResolveRetryWindow(response);

                return PresenceChallengeResult.Failed(
                    HealthProbeState.UnhealthyStatus,
                    "Arcanum's local credential proof is temporarily throttled.",
                    retryWindow.RetryNotBefore,
                    retryWindow.Delay);
            }

            if (response.StatusCode != HttpStatusCode.NoContent)
            {
                return PresenceChallengeResult.Failed(
                    HealthProbeState.UnexpectedResponder,
                    UnexpectedResponderGuidance);
            }

            if (!ArcanumPresenceProofProtocol.TryCanonicalAuthority(
                    _presenceUri,
                    out string? expectedAuthority)
                || !TryGetSingleHeader(
                    response,
                    ArcanumApiHeaders.PresenceVersion,
                    out string? version)
                || !string.Equals(version, ArcanumPresenceProofProtocol.Version, StringComparison.Ordinal)
                || !TryGetSingleHeader(
                    response,
                    ArcanumApiHeaders.PresenceAuthority,
                    out string? authority)
                || !string.Equals(authority, expectedAuthority, StringComparison.Ordinal))
            {
                return PresenceChallengeResult.Failed(
                    HealthProbeState.UnexpectedResponder,
                    UnexpectedResponderGuidance);
            }

            byte[]? capabilityEnvelope = null;
            byte[]? proof = null;

            try
            {
                if (!TryGetSingleHeader(
                        response,
                        ArcanumApiHeaders.PresenceCapability,
                        out string? encodedCapability)
                    || !ArcanumPresenceProofProtocol.TryDecode(
                        encodedCapability,
                        ArcanumPresenceProofProtocol.CapabilityEnvelopeBytes,
                        out capabilityEnvelope)
                    || !TryGetSingleHeader(
                        response,
                        ArcanumApiHeaders.PresenceProof,
                        out string? encodedProof)
                    || !ArcanumPresenceProofProtocol.TryDecode(
                        encodedProof,
                        ArcanumPresenceProofProtocol.ProofBytes,
                        out proof))
                {
                    return PresenceChallengeResult.Failed(
                        HealthProbeState.UnexpectedResponder,
                        UnexpectedResponderGuidance);
                }

                PresenceChallengeResult challenge = PresenceChallengeResult.Succeeded(
                    authority!,
                    capabilityEnvelope!,
                    proof!,
                    requestStartedTimestamp);

                capabilityEnvelope = null;
                proof = null;

                return challenge;
            }
            finally
            {
                if (capabilityEnvelope is not null)
                {
                    CryptographicOperations.ZeroMemory(capabilityEnvelope);
                }

                if (proof is not null)
                {
                    CryptographicOperations.ZeroMemory(proof);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return PresenceChallengeResult.Failed(
                HealthProbeState.Timeout,
                "Presence proof timed out before a credential was read.");
        }
        catch (HttpRequestException exception)
        {
            return PresenceChallengeResult.Failed(
                ArcanumHealthProbe.ClassifyHttpRequestException(exception),
                "Arcanum's local presence endpoint could not be reached.");
        }
        catch (IOException)
        {
            return PresenceChallengeResult.Failed(
                HealthProbeState.UnexpectedResponder,
                UnexpectedResponderGuidance);
        }
    }

    private static async Task<SecretStoreReadResult> ReadCredentialAsync(
        Func<CancellationToken, Task<SecretStoreReadResult>> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            SecretStoreReadResult result = await reader(cancellationToken).ConfigureAwait(false);

            if (result.Status == SecretStoreReadStatus.Ok
                && string.IsNullOrWhiteSpace(result.Value))
            {
                return SecretStoreReadResult.Corrupted(
                    "Credential storage returned an empty value.");
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return SecretStoreReadResult.Corrupted(
                "Credential storage could not be read.");
        }
    }

    private PresenceRetryWindow ResolveRetryWindow(HttpResponseMessage response)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        TimeSpan retryAfter = MinimumPresenceRetryAfter;

        if (response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? values))
        {
            string[] materialized = [.. values];

            if (materialized.Length == 1)
            {
                string raw = materialized[0];

                if (long.TryParse(
                        raw,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out long seconds)
                    && seconds >= 0)
                {
                    retryAfter = seconds >= MaximumPresenceRetryAfter.TotalSeconds
                        ? MaximumPresenceRetryAfter
                        : TimeSpan.FromSeconds(seconds);
                }
                else if (DateTimeOffset.TryParseExact(
                        raw,
                        "r",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTimeOffset retryDate))
                {
                    retryAfter = retryDate - now;
                }
            }
        }

        retryAfter = retryAfter < MinimumPresenceRetryAfter
            ? MinimumPresenceRetryAfter
            : retryAfter > MaximumPresenceRetryAfter
                ? MaximumPresenceRetryAfter
                : retryAfter;

        return new PresenceRetryWindow(now + retryAfter, retryAfter);
    }

    private bool TryUnlockCapability(
        SecretStoreReadResult read,
        byte[] nonce,
        PresenceChallengeResult challenge,
        out string? processCapability,
        out DateTimeOffset renewAfter,
        out DateTimeOffset expiresAt,
        out ApiCredentialCapabilityWindow capabilityWindow,
        out byte[]? keyDigest)
    {
        processCapability = null;
        renewAfter = default;
        expiresAt = default;
        capabilityWindow = default;
        keyDigest = null;

        if (read.Status != SecretStoreReadStatus.Ok
            || string.IsNullOrWhiteSpace(read.Value))
        {
            return false;
        }

        byte[] encoded = Encoding.UTF8.GetBytes(read.Value);
        byte[]? digest = null;

        try
        {
            digest = SHA256.HashData(encoded);

            if (!TryDecryptVerifiedCapability(
                    digest,
                    nonce,
                    challenge,
                    out processCapability,
                    out renewAfter,
                    out expiresAt,
                    out capabilityWindow))
            {
                return false;
            }

            keyDigest = digest;
            digest = null;

            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);

            if (digest is not null)
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
    }

    private bool TryDecryptVerifiedCapability(
        ReadOnlySpan<byte> keyDigest,
        ReadOnlySpan<byte> nonce,
        PresenceChallengeResult challenge,
        out string? processCapability,
        out DateTimeOffset renewAfter,
        out DateTimeOffset expiresAt,
        out ApiCredentialCapabilityWindow capabilityWindow)
    {
        processCapability = null;
        renewAfter = default;
        expiresAt = default;
        capabilityWindow = default;

        if (!ArcanumPresenceProofProtocol.VerifyProof(
                keyDigest,
                nonce,
                challenge.Authority!,
                challenge.CapabilityEnvelope,
                challenge.Proof)
            || !ArcanumPresenceProofProtocol.TryDecryptCapabilityEnvelope(
                keyDigest,
                nonce,
                challenge.Authority!,
                challenge.CapabilityEnvelope,
                out byte[]? capability))
        {
            return false;
        }

        try
        {
            if (!ArcanumPresenceProofProtocol.TryReadCapabilityValidityWindow(
                    capability,
                    out DateTimeOffset issuedAt,
                    out DateTimeOffset parsedExpiresAt))
            {
                return false;
            }

            TimeSpan renewalLead = TimeSpan.FromSeconds(Math.Min(
                60,
                Math.Max(1, (parsedExpiresAt - issuedAt).TotalSeconds / 2)));

            renewAfter = parsedExpiresAt - renewalLead;
            expiresAt = parsedExpiresAt;
            capabilityWindow = new ApiCredentialCapabilityWindow(
                challenge.RequestStartedTimestamp,
                (parsedExpiresAt - issuedAt) - renewalLead,
                parsedExpiresAt - issuedAt);
            processCapability = ArcanumPresenceProofProtocol.Encode(capability);

            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(capability);
        }
    }

    private void CacheDigestAndZero(byte[] digest)
    {
        byte[]? replacement = null;
        byte[]? previous = null;
        bool published = false;

        try
        {
            replacement = digest.ToArray();

            lock (_digestSync)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    throw new ObjectDisposedException(nameof(ArcanumApiCredentialLease));
                }

                previous = _keyDigest;
                _keyDigest = replacement;
                published = true;
            }

            if (previous is not null)
            {
                CryptographicOperations.ZeroMemory(previous);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);

            if (!published && replacement is not null)
            {
                CryptographicOperations.ZeroMemory(replacement);
            }
        }
    }

    private byte[]? CopyDigest()
    {
        lock (_digestSync)
        {
            return _keyDigest?.ToArray();
        }
    }

    private static SecretStoreReadStatus CombineCredentialStatus(
        SecretStoreReadResult mirror,
        SecretStoreReadResult primary)
    {
        if (mirror.Status == SecretStoreReadStatus.Ok
            || primary.Status == SecretStoreReadStatus.Ok)
        {
            return SecretStoreReadStatus.Ok;
        }

        if (mirror.Status == SecretStoreReadStatus.Corrupted
            || primary.Status == SecretStoreReadStatus.Corrupted)
        {
            return SecretStoreReadStatus.Corrupted;
        }

        return SecretStoreReadStatus.Missing;
    }

    private static HttpClient CreateHttpClient(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        return httpClientFactory.CreateClient(ArcanumApiClient.RequestHttpClientName);
    }

    private static Uri CreatePresenceUri(IOptionsMonitor<ArcanumSettings> settingsMonitor)
    {
        ArgumentNullException.ThrowIfNull(settingsMonitor);

        return new Uri(ArcanumLocalApiAddress.ResolvePresenceProbeUrl(
            settingsMonitor.CurrentValue.Host));
    }

    private static Func<CancellationToken, Task<SecretStoreReadResult>> CreateMirrorReader(
        DataProtectionSecretStore mirrorStore)
    {
        ArgumentNullException.ThrowIfNull(mirrorStore);

        return cancellationToken => mirrorStore
            .GetApiKeyReadResultAsync()
            .WaitAsync(cancellationToken);
    }

    private static Func<CancellationToken, Task<SecretStoreReadResult>> CreatePrimaryReader(
        ISecretStore primaryStore)
    {
        ArgumentNullException.ThrowIfNull(primaryStore);

        return cancellationToken => primaryStore
            .PeekApiKeyReadResultAsync()
            .WaitAsync(cancellationToken);
    }

    private static bool TryGetSingleHeader(
        HttpResponseMessage response,
        string name,
        out string? value)
    {
        value = null;

        if (!response.Headers.TryGetValues(name, out IEnumerable<string>? values))
        {
            return false;
        }

        StringValues materialized = new(values.ToArray());

        if (materialized.Count != 1
            || string.IsNullOrWhiteSpace(materialized[0]))
        {
            return false;
        }

        value = materialized[0];

        return true;
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The credential-resolution timeout must be positive.");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class ResolutionFlight(
        TimeSpan timeout,
        bool isProactiveRenewal)
    {
        internal CancellationTokenSource Cancellation { get; } = new();

        internal TaskCompletionSource<ApiCredentialLeaseResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TimeSpan Timeout { get; } = timeout;

        internal bool IsProactiveRenewal { get; } = isProactiveRenewal;

        internal bool CachedCapabilityWasRejected { get; set; }

        internal int WaiterCount { get; set; }
    }

    private sealed record FlightWait(
        ResolutionFlight Flight,
        ApiCredentialLeaseResult? Fallback,
        bool StartsFlight);

    private sealed class CredentialResolution : IDisposable
    {
        private byte[]? _digest;

        private CredentialResolution(
            ApiCredentialLeaseResult result,
            byte[]? digest)
        {
            Result = result;
            _digest = digest;
        }

        internal ApiCredentialLeaseResult Result { get; }

        internal static CredentialResolution WithDigest(
            ApiCredentialLeaseResult result,
            byte[] digest) =>
            new(result, digest);

        internal static CredentialResolution WithoutDigest(
            ApiCredentialLeaseResult result) =>
            new(result, null);

        internal byte[]? TakeDigest()
        {
            byte[]? digest = _digest;
            _digest = null;

            return digest;
        }

        public void Dispose()
        {
            if (_digest is not null)
            {
                CryptographicOperations.ZeroMemory(_digest);
                _digest = null;
            }
        }
    }

    private sealed class CachedLeaseState
    {
        private CachedLeaseState(
            ApiCredentialLeaseResult result,
            long retryStartedTimestamp,
            TimeSpan? retryDelay)
        {
            Result = result;
            RetryStartedTimestamp = retryStartedTimestamp;
            RetryDelay = retryDelay;
        }

        internal ApiCredentialLeaseResult Result { get; }

        internal long RetryStartedTimestamp { get; private set; }

        internal TimeSpan? RetryDelay { get; private set; }

        internal ApiCredentialLeaseResult? RetryFailure { get; private set; }

        internal bool IsCapabilityUnexpired(
            TimeProvider timeProvider,
            long nowTimestamp) =>
            Result.CapabilityWindow is { } window
            && HasNotElapsed(
                timeProvider,
                window.RequestStartedTimestamp,
                nowTimestamp,
                window.ExpiresAfterElapsed);

        internal bool IsBeforeRenewal(
            TimeProvider timeProvider,
            long nowTimestamp) =>
            Result.CapabilityWindow is { } window
            && HasNotElapsed(
                timeProvider,
                window.RequestStartedTimestamp,
                nowTimestamp,
                window.RenewAfterElapsed);

        internal static CachedLeaseState Create(
            ApiCredentialLeaseResult result,
            TimeProvider timeProvider) =>
            new(
                result,
                result.RetryDelay is null ? 0 : timeProvider.GetTimestamp(),
                result.RetryDelay);

        internal void SetRetryDelay(
            long startedTimestamp,
            TimeSpan retryDelay,
            ApiCredentialLeaseResult retryFailure)
        {
            RetryStartedTimestamp = startedTimestamp;
            RetryDelay = retryDelay;
            RetryFailure = retryFailure;
        }

        internal void ClearRetryDelay()
        {
            RetryStartedTimestamp = 0;
            RetryDelay = null;
            RetryFailure = null;
        }

        private static bool HasNotElapsed(
            TimeProvider timeProvider,
            long startedTimestamp,
            long nowTimestamp,
            TimeSpan delay)
        {
            TimeSpan elapsed = timeProvider.GetElapsedTime(
                startedTimestamp,
                nowTimestamp);

            return elapsed >= TimeSpan.Zero && elapsed < delay;
        }
    }

    private readonly record struct PresenceRetryWindow(
        DateTimeOffset RetryNotBefore,
        TimeSpan Delay);

    private sealed class PresenceChallengeResult : IDisposable
    {
        private PresenceChallengeResult(
            string? authority,
            byte[] capabilityEnvelope,
            byte[] proof,
            ApiCredentialLeaseResult? failure,
            long requestStartedTimestamp)
        {
            Authority = authority;
            CapabilityEnvelope = capabilityEnvelope;
            Proof = proof;
            Failure = failure;
            RequestStartedTimestamp = requestStartedTimestamp;
        }

        internal bool IsUsable => Failure is null;
        internal string? Authority { get; }
        internal byte[] CapabilityEnvelope { get; }
        internal byte[] Proof { get; }
        internal ApiCredentialLeaseResult? Failure { get; }
        internal long RequestStartedTimestamp { get; }

        internal static PresenceChallengeResult Succeeded(
            string authority,
            byte[] capabilityEnvelope,
            byte[] proof,
            long requestStartedTimestamp) =>
            new(
                authority,
                capabilityEnvelope,
                proof,
                null,
                requestStartedTimestamp);

        internal static PresenceChallengeResult Failed(
            HealthProbeState state,
            string guidance,
            DateTimeOffset? retryNotBefore = null,
            TimeSpan? retryDelay = null) =>
            new(
                null,
                [],
                [],
                ApiCredentialLeaseResult.Transient(
                    state,
                    guidance,
                    retryNotBefore,
                    retryDelay),
                requestStartedTimestamp: 0);

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(CapabilityEnvelope);
            CryptographicOperations.ZeroMemory(Proof);
        }
    }
}

internal readonly record struct ApiCredentialCapabilityWindow(
    long RequestStartedTimestamp,
    TimeSpan RenewAfterElapsed,
    TimeSpan ExpiresAfterElapsed);

internal sealed record ApiCredentialLeaseResult(
    bool IsVerified,
    string? ProcessCapability,
    DateTimeOffset? RenewAfter,
    DateTimeOffset? ExpiresAt,
    ApiCredentialCapabilityWindow? CapabilityWindow,
    HealthProbeState ProbeState,
    SecretStoreReadStatus? CredentialStatus,
    string? Guidance,
    DateTimeOffset? RetryNotBefore,
    TimeSpan? RetryDelay)
{
    internal static ApiCredentialLeaseResult Verified(
        string processCapability,
        DateTimeOffset renewAfter,
        DateTimeOffset expiresAt,
        ApiCredentialCapabilityWindow capabilityWindow) =>
        new(
            true,
            processCapability,
            renewAfter,
            expiresAt,
            capabilityWindow,
            HealthProbeState.Healthy,
            SecretStoreReadStatus.Ok,
            null,
            null,
            null);

    internal static ApiCredentialLeaseResult CredentialFailure(
        SecretStoreReadStatus status,
        string guidance) =>
        new(
            false,
            null,
            null,
            null,
            null,
            HealthProbeState.Unauthorized,
            status,
            guidance,
            null,
            null);

    internal static ApiCredentialLeaseResult Transient(
        HealthProbeState state,
        string guidance,
        DateTimeOffset? retryNotBefore = null,
        TimeSpan? retryDelay = null) =>
        new(
            false,
            null,
            null,
            null,
            null,
            state,
            null,
            guidance,
            retryNotBefore,
            retryDelay);
}
