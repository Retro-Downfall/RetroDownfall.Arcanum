using System.Collections.Concurrent;

using A2A;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>
/// Default <see cref="IA2AClientService"/> (the Archmage Client). Discovers remote Agent Cards, validates
/// outbound URLs against <see cref="OutboundUrlGuard"/> and the optional <c>AllowedRemoteAgents</c> allowlist,
/// and performs a blocking A2A message exchange until completion or caller/host cancellation.
/// </summary>
/// <remarks>
/// Concurrency is governed by an in-memory <see cref="SemaphoreSlim"/> sized from the retained
/// host-capacity policy at first use. Excess work waits behind that admission boundary until a slot
/// opens or its caller cancels; the capacity is not a total-work rejection rule.
/// </remarks>
public sealed class A2AClientService : IA2AClientService
{

    public const string OutboundHttpClientName = "A2AOutbound";

    /// <summary>
    /// Bound on establishing the TCP connection to a remote agent. This is connection setup only — a
    /// dispatched Sending itself has no whole-operation deadline and ends on completion, remote failure,
    /// or caller/host cancellation (issue #55).
    /// </summary>
    public static readonly TimeSpan OutboundConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Default header the peer credential is sent in — Arcanum's own API-key header.</summary>
    public const string DefaultOutboundCredentialHeader = "X-Arcanum-Key";

    private const string WellKnownAgentCardPath = "/.well-known/agent-card.json";

    /// <summary>
    /// Bound on the Agent Card cache. Keys come from model-supplied URLs, so this is a memory guard on a
    /// derived index — not a limit on how many distinct agents may be dispatched to.
    /// </summary>
    private const int MaxCachedCards = 256;

    private static readonly TimeSpan CardCacheTtl = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan InitialPollInterval = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Bound on the best-effort peer-cancel call issued after the caller's token is already cancelled.</summary>
    private static readonly TimeSpan RemoteCancelTimeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly IOptionsMonitor<ArcanumSettings> _options;

    private readonly ILogger<A2AClientService> _logger;

    private readonly ConcurrentDictionary<string, CachedCard> _cardCache = new(StringComparer.Ordinal);

    private readonly Lazy<SemaphoreSlim> _gate;

    public A2AClientService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<ArcanumSettings> options,
        ILogger<A2AClientService> logger)
    {
        _httpClientFactory = httpClientFactory;

        _options = options;

        _logger = logger;

        _gate = new Lazy<SemaphoreSlim>(() =>
        {
            int max = ArcanumSettingClamps.MaxConcurrentA2ATasks(
                _options.CurrentValue.Execution.MaxConcurrentA2ATasks);

            return new SemaphoreSlim(max, max);
        });

    }

    public async Task<Result<A2ADispatchResult>> DispatchSendingAsync(
        string goal,
        string? name,
        string agentUrl,
        IReadOnlyList<string>? delegationChain = null,
        CancellationToken cancellationToken = default)
    {

        ArcanumSettings settings = _options.CurrentValue;

        ConclaveA2ASettings a2a = settings.ResolveA2A();

        if (!settings.ResolveConclave().Enabled || !a2a.Enabled || !a2a.ClientEnabled)
        {

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.Disabled, "A2A is disabled; dispatch_sending is not available."));

        }

        if (string.IsNullOrWhiteSpace(goal))
        {

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Apprentice.InvalidGoal, "A non-empty goal is required to dispatch a Sending."));

        }

        if (string.IsNullOrWhiteSpace(agentUrl) || !Uri.TryCreate(agentUrl.Trim(), UriKind.Absolute, out _))
        {

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.AgentCardInvalid, "A non-empty absolute agent_url is required to dispatch a Sending."));

        }

        string trimmedUrl = agentUrl.Trim();

        string[] allowlist = a2a.AllowedRemoteAgents ?? [];

        if (allowlist.Length > 0 && !IsAllowedAgent(trimmedUrl, allowlist))
        {

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.AgentNotAllowed, $"Remote agent '{trimmedUrl}' is not in the configured AllowedRemoteAgents allowlist."));

        }

        Result discoveryUrlCheck = await OutboundUrlGuard
            .ValidateUntrustedUrlAsync(trimmedUrl, cancellationToken)
            .ConfigureAwait(false);

        if (discoveryUrlCheck.IsFailure)
        {

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.AgentUnreachable, $"Remote agent URL rejected by outbound URL policy: {discoveryUrlCheck.Error.Message}"));

        }

        SemaphoreSlim gate = _gate.Value;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            return await DispatchInternalAsync(
                    goal.Trim(),
                    trimmedUrl,
                    allowlist,
                    delegationChain ?? [],
                    cancellationToken)
                .ConfigureAwait(false);

        }
        finally
        {

            // Released here regardless of success, remote failure, or caller/host cancellation.
            gate.Release();

        }

    }

    private async Task<Result<A2ADispatchResult>> DispatchInternalAsync(
        string goal,
        string discoveryUrl,
        string[] allowlist,
        IReadOnlyList<string> delegationChain,
        CancellationToken cancellationToken)
    {

        AgentCard card;

        try
        {

            card = await ResolveCardAsync(discoveryUrl, cancellationToken).ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is HttpRequestException or A2AException or InvalidOperationException)
        {

            _logger.LogWarning(ex, "dispatch_sending: failed to resolve Agent Card at {AgentUrl}.", discoveryUrl);

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.AgentCardInvalid, $"Could not resolve the remote agent's Agent Card: {ex.Message}"));

        }

        AgentInterface[] interfaces = [.. (card.SupportedInterfaces ?? []).Where(static i => !string.IsNullOrWhiteSpace(i.Url))];

        if (interfaces.Length == 0)
        {

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.AgentCardInvalid, "The remote Agent Card did not advertise a usable interface."));

        }

        // EVERY advertised interface is checked, not just the first: A2AClientFactory selects by protocol
        // binding preference rather than position, so validating one entry would let a hostile card steer
        // the connection to an unchecked URL. All of these are remote-controlled values.
        foreach (AgentInterface advertised in interfaces)
        {

            string interfaceUrl = advertised.Url!;

            if (allowlist.Length > 0 && !IsAllowedAgent(interfaceUrl, allowlist))
            {

                return Result<A2ADispatchResult>.Failure(
                    new Error(ErrorCodes.Sending.AgentNotAllowed, $"Remote agent interface '{interfaceUrl}' is not in the configured AllowedRemoteAgents allowlist."));

            }

            Result interfaceUrlCheck = await OutboundUrlGuard
                .ValidateUntrustedUrlAsync(interfaceUrl, cancellationToken)
                .ConfigureAwait(false);

            if (interfaceUrlCheck.IsFailure)
            {

                return Result<A2ADispatchResult>.Failure(
                    new Error(ErrorCodes.Sending.AgentUnreachable, $"Remote agent interface rejected by outbound URL policy: {interfaceUrlCheck.Error.Message}"));

            }

        }

        HttpClient httpClient = CreateOutboundClient(CredentialTargetForCard(card, discoveryUrl, allowlist));

        IA2AClient client;

        try
        {

            client = A2AClientFactory.Create(card, httpClient);

        }
        catch (Exception ex) when (ex is A2AException or ArgumentException or InvalidOperationException)
        {

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.AgentCardInvalid, $"The remote Agent Card advertises no protocol binding this client supports: {ex.Message}"));

        }

        Message message = new()
        {
            Role = Role.User,
            MessageId = Guid.NewGuid().ToString("N"),
            Parts = [Part.FromText(goal)],
        };

        // Loop prevention: the receiving agent refuses work whose chain already contains its own node.
        ConclaveDelegationChain.Write(message, ConclaveDelegationChain.Extend(delegationChain));

        // ReturnImmediately hands back the remote task id before the work finishes. That id is what makes
        // local cancellation propagatable — a blocking send abandons the HTTP call without ever learning
        // which remote task to cancel, leaving the peer running and billing (issue #12).
        SendMessageRequest sendRequest = new()
        {
            Message = message,
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        };

        SendMessageResponse response;

        try
        {

            response = await client.SendMessageAsync(sendRequest, cancellationToken).ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is HttpRequestException or A2AException)
        {

            _logger.LogWarning(ex, "dispatch_sending: failed to send message to the remote agent.");

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.AgentUnreachable, $"Failed to send the Sending to the remote agent: {ex.Message}"));

        }

        if (response.PayloadCase == SendMessageResponseCase.Message)
        {

            return Result<A2ADispatchResult>.Success(new A2ADispatchResult(null, ExtractText(response.Message!.Parts)));

        }

        AgentTask task = response.Task
            ?? throw new InvalidOperationException("A2A SendMessageResponse carried neither a Message nor a Task payload.");

        try
        {

            task = await PollUntilSettledAsync(client, task, cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            await TryCancelRemoteTaskAsync(client, task.Id).ConfigureAwait(false);

            throw;

        }
        catch (Exception ex) when (ex is HttpRequestException or A2AException)
        {

            // The remote accepted the task and then the transport failed. The work may still be running
            // there, so report the task id rather than letting an exception escape as an opaque tool error.
            _logger.LogWarning(ex, "dispatch_sending: lost contact with the remote agent while polling task {TaskId}.", task.Id);

            return Result<A2ADispatchResult>.Failure(new Error(
                ErrorCodes.Sending.AgentUnreachable,
                $"Lost contact with the remote agent while awaiting task '{task.Id}': {ex.Message}. "
                + "The remote task may still be running; it was not cancelled."));

        }

        if (task.Status.State == TaskState.Completed)
        {

            return Result<A2ADispatchResult>.Success(new A2ADispatchResult(task.Id, ExtractTaskText(task)));

        }

        string reason = task.Status.Message is { } statusMessage
            ? ExtractText(statusMessage.Parts)
            : $"Remote task ended in state {task.Status.State}.";

        if (task.Status.State is TaskState.InputRequired or TaskState.AuthRequired)
        {

            // Neither state is terminal in A2A, but a blocking Sending has no way to supply the follow-up
            // input or credential, so waiting is an infinite poll that also pins a concurrency slot. End it
            // with a reason the Mage can act on. Interactive continuation is tracked in issue #64.
            await TryCancelRemoteTaskAsync(client, task.Id).ConfigureAwait(false);

            string need = task.Status.State == TaskState.InputRequired
                ? "asked for more input"
                : "asked for authentication";

            return Result<A2ADispatchResult>.Failure(new Error(
                ErrorCodes.Sending.TaskRejected,
                $"The remote agent {need} before it could finish, which a blocking Sending cannot supply: {reason}"));

        }

        return Result<A2ADispatchResult>.Failure(new Error(ErrorCodes.Sending.TaskRejected, reason));

    }

    /// <summary>
    /// Polls the remote task until it settles: an A2A terminal state, or <c>input-required</c> /
    /// <c>auth-required</c>, which are not terminal but which a blocking Sending can never move past.
    /// </summary>
    /// <remarks>
    /// The delay backs off from <see cref="InitialPollInterval"/> to <see cref="MaxPollInterval"/> so a fast
    /// remote is observed promptly without hammering a slow one. There is no poll-count or elapsed ceiling:
    /// the Sending ends on a settled remote state or caller/host cancellation (issue #55).
    /// </remarks>
    private static async Task<AgentTask> PollUntilSettledAsync(
        IA2AClient client,
        AgentTask task,
        CancellationToken cancellationToken)
    {

        TimeSpan delay = InitialPollInterval;

        while (!IsSettled(task.Status.State))
        {

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            delay = delay < MaxPollInterval
                ? TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxPollInterval.Ticks))
                : MaxPollInterval;

            task = await client
                .GetTaskAsync(new GetTaskRequest { Id = task.Id }, cancellationToken)
                .ConfigureAwait(false);

        }

        return task;

    }

    private static bool IsSettled(TaskState state) =>
        TaskStateExtensions.IsTerminal(state)
        || state is TaskState.InputRequired or TaskState.AuthRequired;

    /// <summary>
    /// Best-effort peer cancellation. Runs on its own short-lived token because the caller's token is
    /// already cancelled, and never rethrows: the local operation is ending either way, and a remote that
    /// refuses or has already finished is not a local failure.
    /// </summary>
    private async Task TryCancelRemoteTaskAsync(IA2AClient client, string taskId)
    {

        using CancellationTokenSource cleanupScope = new(RemoteCancelTimeout);

        try
        {

            await client
                .CancelTaskAsync(new CancelTaskRequest { Id = taskId }, cleanupScope.Token)
                .ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            _logger.LogInformation(
                ex,
                "dispatch_sending: could not cancel remote A2A task {TaskId} after local cancellation.",
                taskId);

        }

    }

    /// <summary>
    /// Decides whether the peer credential may travel to the interfaces a remote Agent Card advertises.
    /// </summary>
    /// <returns>
    /// A representative target URL when <em>every</em> advertised interface shares the discovery origin or is
    /// explicitly allowlisted; otherwise <c>null</c>, meaning "dispatch without a credential".
    /// </returns>
    /// <remarks>
    /// The card is remote-controlled. A hostile peer that advertises an interface on a host it also controls
    /// would otherwise be handed the operator's credential — the SSRF guard and allowlist stop us connecting
    /// to a <em>blocked</em> host, but with the default empty allowlist any public host is reachable. The
    /// check covers every interface because <c>A2AClientFactory</c> selects one by protocol-binding
    /// preference rather than position, so a per-interface decision could be made about a URL the SDK does
    /// not end up using.
    /// </remarks>
    private static string? CredentialTargetForCard(AgentCard card, string discoveryUrl, string[] allowlist)
    {

        if (!Uri.TryCreate(discoveryUrl, UriKind.Absolute, out Uri? discovery))
        {

            return null;

        }

        string discoveryOrigin = discovery.GetLeftPart(UriPartial.Authority);

        string? representative = null;

        foreach (AgentInterface advertised in card.SupportedInterfaces ?? [])
        {

            if (string.IsNullOrWhiteSpace(advertised.Url))
            {

                continue;

            }

            if (!Uri.TryCreate(advertised.Url, UriKind.Absolute, out Uri? target))
            {

                return null;

            }

            bool vouchedFor = string.Equals(
                    target.GetLeftPart(UriPartial.Authority),
                    discoveryOrigin,
                    StringComparison.OrdinalIgnoreCase)
                || (allowlist.Length > 0 && IsAllowedAgent(advertised.Url, allowlist));

            if (!vouchedFor)
            {

                return null;

            }

            representative ??= advertised.Url;

        }

        return representative;

    }

    /// <summary>
    /// Resolves the remote Agent Card, accepting either an explicit card URL or a base URL.
    /// </summary>
    /// <remarks>
    /// An explicit path (<c>https://peer/api/conclave/a2a/agent-card</c>) is fetched exactly as given. A
    /// bare origin falls back to the SDK's well-known path and then to Arcanum's own default card path,
    /// because Arcanum deliberately serves no unauthenticated <c>/.well-known/agent-card.json</c> — probing
    /// only the well-known path made one Arcanum unable to discover another at all (issue #12).
    /// </remarks>
    private async Task<AgentCard> ResolveCardAsync(string discoveryUrl, CancellationToken cancellationToken)
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (_cardCache.TryGetValue(discoveryUrl, out CachedCard? cached) && cached.ExpiresAt > now)
        {

            return cached.Card;

        }

        Uri uri = new(discoveryUrl);

        string origin = uri.GetLeftPart(UriPartial.Authority);

        string path = uri.PathAndQuery;

        string[] candidatePaths = path.Length > 1
            ? [path]
            : [WellKnownAgentCardPath, ArcanumRuntimeDefaults.Conclave.A2A.ServerPath + "/agent-card"];

        // The operator typed this URL, so it is a vouched-for credential target.
        HttpClient httpClient = CreateOutboundClient(discoveryUrl);

        Exception? lastFailure = null;

        foreach (string candidate in candidatePaths)
        {

            try
            {

                A2ACardResolver resolver = new(new Uri(origin), httpClient, candidate);

                AgentCard card = await resolver.GetAgentCardAsync(cancellationToken).ConfigureAwait(false);

                CacheCard(discoveryUrl, card, now);

                return card;

            }
            catch (Exception ex) when (ex is HttpRequestException or A2AException)
            {

                lastFailure = ex;

            }

        }

        throw lastFailure ?? new A2AException("The remote agent did not serve an Agent Card.");

    }

    /// <summary>
    /// Stores a resolved card and sweeps expired entries. The cache is keyed on a model-supplied URL, so it
    /// is bounded rather than left to grow with every distinct string an Apprentice dispatches to.
    /// </summary>
    private void CacheCard(string discoveryUrl, AgentCard card, DateTimeOffset now)
    {

        if (_cardCache.Count >= MaxCachedCards)
        {

            foreach (KeyValuePair<string, CachedCard> entry in _cardCache)
            {

                if (entry.Value.ExpiresAt <= now)
                {

                    _cardCache.TryRemove(entry.Key, out _);

                }

            }

            // Still full of live entries: drop the soonest-to-expire so the cache cannot grow without bound.
            while (_cardCache.Count >= MaxCachedCards)
            {

                KeyValuePair<string, CachedCard> oldest = _cardCache
                    .OrderBy(static e => e.Value.ExpiresAt)
                    .FirstOrDefault();

                if (oldest.Key is null || !_cardCache.TryRemove(oldest.Key, out _))
                {

                    break;

                }

            }

        }

        _cardCache[discoveryUrl] = new CachedCard(card, now.Add(CardCacheTtl));

    }

    /// <summary>
    /// Builds the outbound client, attaching the operator-configured peer credential when one is set and the
    /// target is one the operator actually vouched for. The credential is read from the environment at
    /// dispatch time and never stored in configuration.
    /// </summary>
    /// <param name="credentialTarget">
    /// The URL this client will talk to, or <c>null</c> for a client that carries no credential.
    /// </param>
    /// <remarks>
    /// The Agent Card is remote-controlled, so a hostile card could advertise an interface on a third-party
    /// host. Sending the peer credential there would hand it to an attacker, so it travels only to the origin
    /// the operator typed or to an explicitly allowlisted target.
    /// </remarks>
    private HttpClient CreateOutboundClient(string? credentialTarget)
    {

        HttpClient httpClient = _httpClientFactory.CreateClient(OutboundHttpClientName);

        ConclaveA2ASettings a2a = _options.CurrentValue.ResolveA2A();

        if (credentialTarget is null || string.IsNullOrWhiteSpace(a2a.OutboundCredentialEnvironmentVariable))
        {

            return httpClient;

        }

        string? credential = System.Environment.GetEnvironmentVariable(
            a2a.OutboundCredentialEnvironmentVariable.Trim());

        if (string.IsNullOrWhiteSpace(credential))
        {

            _logger.LogWarning(
                "dispatch_sending: Arcanum:Integrations:A2A:OutboundCredentialEnvironmentVariable names "
                + "'{EnvironmentVariable}', but it is not set; dispatching without a peer credential.",
                a2a.OutboundCredentialEnvironmentVariable);

            return httpClient;

        }

        string header = string.IsNullOrWhiteSpace(a2a.OutboundCredentialHeader)
            ? DefaultOutboundCredentialHeader
            : a2a.OutboundCredentialHeader.Trim();

        httpClient.DefaultRequestHeaders.Remove(header);

        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header, credential);

        return httpClient;

    }

    private static bool IsAllowedAgent(string url, string[] allowlist)
    {

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {

            return false;

        }

        foreach (string entry in allowlist)
        {

            if (string.IsNullOrWhiteSpace(entry))
            {

                continue;

            }

            string trimmed = entry.Trim();

            if (string.Equals(trimmed, url, StringComparison.OrdinalIgnoreCase))
            {

                return true;

            }

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? allowedUri)
                && string.Equals(
                    allowedUri.GetLeftPart(UriPartial.Authority),
                    uri.GetLeftPart(UriPartial.Authority),
                    StringComparison.OrdinalIgnoreCase))
            {

                return true;

            }

        }

        return false;

    }

    private static string ExtractText(IReadOnlyList<Part>? parts)
    {

        if (parts is null || parts.Count == 0)
        {

            return string.Empty;

        }

        IEnumerable<string> textParts = parts
            .Where(static p => p.ContentCase == PartContentCase.Text && !string.IsNullOrEmpty(p.Text))
            .Select(static p => p.Text!);

        return string.Join('\n', textParts);

    }

    private static string ExtractTaskText(AgentTask task)
    {

        Artifact? lastArtifact = task.Artifacts?.LastOrDefault();

        if (lastArtifact is { Parts.Count: > 0 })
        {

            string artifactText = ExtractText(lastArtifact.Parts);

            if (!string.IsNullOrEmpty(artifactText))
            {

                return artifactText;

            }

        }

        if (task.Status.Message is { } statusMessage)
        {

            string statusText = ExtractText(statusMessage.Parts);

            if (!string.IsNullOrEmpty(statusText))
            {

                return statusText;

            }

        }

        return "(The remote agent completed the Sending without a textual response.)";

    }

    private sealed record CachedCard(AgentCard Card, DateTimeOffset ExpiresAt);

}
