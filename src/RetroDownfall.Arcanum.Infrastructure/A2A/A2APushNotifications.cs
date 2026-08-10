using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using A2A;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>
/// The header an A2A push notification carries its per-task secret in, on both sides of the door.
/// </summary>
public static class A2APushNotificationHeaders
{

    /// <summary>Per-task shared secret minted by whichever side registered the callback.</summary>
    public const string NotificationToken = "X-A2A-Notification-Token";

}

/// <summary>
/// Decides whether a peer-supplied push-notification callback may be posted to, and keeps the ones that
/// may.
/// </summary>
/// <remarks>
/// A push config is a remote-controlled URL this instance will make outbound requests to on a schedule
/// the peer chooses, which is an SSRF primitive if left unchecked. It therefore goes through exactly the
/// same gate as any other egress: <see cref="OutboundUrlGuard"/> plus the
/// <c>Arcanum:Integrations:A2A:AllowedRemoteAgents</c> allowlist when one is configured (issue #67,
/// docs/Arcanum.DESIGN.md &#167;11.11).
/// </remarks>
public sealed class A2APushNotificationRegistry(
    IOptionsMonitor<ArcanumSettings> options,
    ILogger<A2APushNotificationRegistry> logger)
{

    /// <summary>
    /// Bound on remembered callbacks. Keys come from peer-supplied task ids, so this is a memory guard
    /// on a derived index rather than a limit on how many Sendings may be in flight; the oldest
    /// registration is evicted, and losing one costs a notification, not the Sending.
    /// </summary>
    private const int MaxRegistrations = 1_024;

    private readonly ConcurrentDictionary<string, Registration> _byTask = new(StringComparer.Ordinal);

    private long _sequence;

    private readonly record struct Registration(PushNotificationConfig Config, long Sequence);

    /// <summary>Whether an operator has turned the push-notification surface on at all.</summary>
    public bool Enabled => options.CurrentValue.ResolveA2A().PushNotificationsEnabled;

    /// <summary>
    /// Validates and remembers a peer's callback for <paramref name="taskId"/>.
    /// </summary>
    public async Task<Result<PushNotificationConfig>> RegisterAsync(
        string taskId,
        PushNotificationConfig? config,
        CancellationToken cancellationToken = default)
    {

        if (!Enabled)
        {

            return Result<PushNotificationConfig>.Failure(new Error(
                ErrorCodes.Sending.PushNotificationsDisabled,
                "Push notifications are not enabled on this Arcanum instance "
                + "(Arcanum:Integrations:A2A:PushNotifications)."));

        }

        if (string.IsNullOrWhiteSpace(taskId) || config is null || string.IsNullOrWhiteSpace(config.Url))
        {

            return Result<PushNotificationConfig>.Failure(new Error(
                ErrorCodes.Sending.PushNotificationRejected,
                "A push-notification config requires a task id and an absolute callback URL."));

        }

        ConclaveA2ASettings a2a = options.CurrentValue.ResolveA2A();

        Result allowed = await ValidateCallbackUrlAsync(config.Url!, a2a, cancellationToken).ConfigureAwait(false);

        if (allowed.IsFailure)
        {

            logger.LogWarning(
                "A2A: refusing a peer push-notification callback for task {TaskId}: {Reason}",
                taskId,
                allowed.Error.Message);

            return Result<PushNotificationConfig>.Failure(allowed.Error);

        }

        PushNotificationConfig stored = new()
        {
            Id = string.IsNullOrWhiteSpace(config.Id) ? Guid.NewGuid().ToString("N") : config.Id,
            Url = config.Url,
            Token = config.Token,
            Authentication = config.Authentication,
        };

        _byTask[taskId] = new Registration(stored, Interlocked.Increment(ref _sequence));

        Evict();

        return Result<PushNotificationConfig>.Success(stored);

    }

    /// <summary>The callback registered for a task, or <c>null</c> when the peer registered none.</summary>
    public PushNotificationConfig? Resolve(string taskId) =>
        _byTask.TryGetValue(taskId, out Registration registration) ? registration.Config : null;

    /// <summary>Every callback registered for a task. A2A allows several; Arcanum keeps the latest.</summary>
    public IReadOnlyList<PushNotificationConfig> List(string taskId) =>
        Resolve(taskId) is { } config ? [config] : [];

    /// <summary>Forgets a task's callback. Idempotent: removing an unknown task is not an error.</summary>
    public void Remove(string taskId) => _byTask.TryRemove(taskId, out _);

    /// <summary>
    /// Applies the outbound URL policy to a callback target.
    /// </summary>
    public static async Task<Result> ValidateCallbackUrlAsync(
        string url,
        ConclaveA2ASettings a2a,
        CancellationToken cancellationToken)
    {

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {

            return Result.Failure(new Error(
                ErrorCodes.Sending.PushNotificationRejected,
                "A push-notification callback URL must be an absolute http(s) URL."));

        }

        string[] allowlist = a2a.AllowedRemoteAgents ?? [];

        if (allowlist.Length > 0 && !IsAllowed(parsed, allowlist))
        {

            return Result.Failure(new Error(
                ErrorCodes.Sending.AgentNotAllowed,
                "The push-notification callback URL is not in the configured AllowedRemoteAgents allowlist."));

        }

        Result guard = await OutboundUrlGuard
            .ValidateUntrustedUrlAsync(parsed.ToString(), cancellationToken)
            .ConfigureAwait(false);

        return guard.IsFailure
            ? Result.Failure(new Error(
                ErrorCodes.Sending.PushNotificationRejected,
                $"The push-notification callback URL was rejected by outbound URL policy: {guard.Error.Message}"))
            : Result.Success();

    }

    private static bool IsAllowed(Uri target, string[] allowlist)
    {

        foreach (string entry in allowlist)
        {

            if (!string.IsNullOrWhiteSpace(entry)
                && Uri.TryCreate(entry.Trim(), UriKind.Absolute, out Uri? allowed)
                && string.Equals(
                    allowed.GetLeftPart(UriPartial.Authority),
                    target.GetLeftPart(UriPartial.Authority),
                    StringComparison.OrdinalIgnoreCase))
            {

                return true;

            }

        }

        return false;

    }

    private void Evict()
    {

        while (_byTask.Count > MaxRegistrations)
        {

            KeyValuePair<string, Registration> oldest = _byTask
                .OrderBy(static entry => entry.Value.Sequence)
                .FirstOrDefault();

            if (oldest.Key is null || !_byTask.TryRemove(oldest.Key, out _))
            {

                return;

            }

        }

    }

}

/// <summary>
/// Posts an inbound task's state transitions to the callback its peer registered.
/// </summary>
/// <remarks>
/// Best-effort by design: a peer whose callback endpoint is down must not fail the Apprentice that is
/// doing the actual work, and a notification is a convenience over the task state the peer can always
/// fetch. Failures are logged, never thrown at the Apprentice loop.
/// </remarks>
public sealed class A2APushNotificationDispatcher(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ArcanumSettings> options,
    ILogger<A2APushNotificationDispatcher> logger)
{

    /// <summary>Bound on one notification POST. A slow callback must not stall the Chronicle relay.</summary>
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(15);

    public async Task NotifyAsync(
        PushNotificationConfig config,
        string taskId,
        string state,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(config.Url))
        {

            return;

        }

        try
        {

            // Re-checked at delivery time, not only at registration: the allowlist and DNS can both
            // change between a peer registering a callback and a long Sending reaching a terminal state.
            Result allowed = await A2APushNotificationRegistry
                .ValidateCallbackUrlAsync(config.Url!, options.CurrentValue.ResolveA2A(), cancellationToken)
                .ConfigureAwait(false);

            if (allowed.IsFailure)
            {

                logger.LogWarning(
                    "A2A: not delivering a push notification for task {TaskId}: {Reason}",
                    taskId,
                    allowed.Error.Message);

                return;

            }

            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            deadline.CancelAfter(DeliveryTimeout);

            HttpClient client = httpClientFactory.CreateClient(A2AClientService.OutboundHttpClientName);

            using HttpRequestMessage request = new(HttpMethod.Post, config.Url);

            request.Content = new StringContent(
                JsonSerializer.Serialize(
                    new A2APushNotificationPayload { TaskId = taskId, State = state },
                    A2APushNotificationJsonContext.Default.A2APushNotificationPayload),
                Encoding.UTF8,
                new MediaTypeHeaderValue("application/json"));

            if (!string.IsNullOrWhiteSpace(config.Token))
            {

                request.Headers.TryAddWithoutValidation(
                    A2APushNotificationHeaders.NotificationToken,
                    config.Token);

            }

            if (config.Authentication is { Scheme.Length: > 0 } auth)
            {

                request.Headers.Authorization = new AuthenticationHeaderValue(auth.Scheme, auth.Credentials);

            }

            using HttpResponseMessage response = await client
                .SendAsync(request, deadline.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {

                logger.LogInformation(
                    "A2A: push notification for task {TaskId} was answered {StatusCode}.",
                    taskId,
                    (int)response.StatusCode);

            }

        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {

            logger.LogInformation(ex, "A2A: could not deliver a push notification for task {TaskId}.", taskId);

        }

    }

}

/// <summary>
/// Wire shape of an Arcanum push notification.
/// </summary>
/// <remarks>
/// Deliberately narrow, for the same reason a <c>sendingProgress</c> Chronicle frame is: the peer's task
/// id and the protocol state name, and nothing that could carry Apprentice prose, tool output, or
/// workspace detail across the door (&#167;5.7.1).
/// </remarks>
public sealed record A2APushNotificationPayload
{

    public string TaskId { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

}

/// <summary>Source-generated contract so push-notification delivery stays Native AOT-safe.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(A2APushNotificationPayload))]
internal sealed partial class A2APushNotificationJsonContext : JsonSerializerContext;

/// <summary>
/// Hashes and compares the per-task secret an outbound callback is authenticated by.
/// </summary>
/// <remarks>
/// The token itself is never persisted — only its digest — so a Grimoire read cannot hand an attacker a
/// working callback credential, and the comparison is constant-time so a wrong token cannot be found by
/// timing (&#167;11.2).
/// </remarks>
public static class A2ACallbackToken
{

    /// <summary>Mints a 256-bit callback secret.</summary>
    public static string Mint() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>The digest stored against a Sending's durable record.</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));

    /// <summary>Constant-time check of a presented token against a stored digest.</summary>
    public static bool Matches(string? presented, string? expectedHash)
    {

        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expectedHash))
        {

            return false;

        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Hash(presented)),
            Encoding.UTF8.GetBytes(expectedHash));

    }

}
