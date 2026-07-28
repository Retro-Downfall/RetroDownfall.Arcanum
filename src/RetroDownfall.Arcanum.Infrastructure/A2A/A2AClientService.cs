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
/// and performs a blocking A2A message exchange bounded by <c>ExternalTaskTimeoutMinutes</c>.
/// </summary>
/// <remarks>
/// Concurrency is governed by an in-memory <see cref="SemaphoreSlim"/> sized from
/// <c>Arcanum:Conclave:A2A:MaxExternalTasks</c> at first use — mirroring <c>ChronicleHub</c>'s per-apprentice
/// hub capacity, a running instance picks up a new limit only after a restart. There is no persisted or
/// repository-backed counter: external A2A tasks are never written to the Grimoire (see persistence.md), so
/// unlike <c>ConclaveLineage</c>'s breadth check this cannot be enforced via a repository query.
/// </remarks>
public sealed class A2AClientService : IA2AClientService
{

    public const string OutboundHttpClientName = "A2AOutbound";

    private static readonly TimeSpan CardCacheTtl = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

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
            int max = ArcanumSettingClamps.MaxExternalTasks(
                (_options.CurrentValue.Conclave.A2A ?? new ConclaveA2ASettings()).MaxExternalTasks);

            return new SemaphoreSlim(max, max);
        });

    }

    public async Task<Result<A2ADispatchResult>> DispatchSendingAsync(
        string goal,
        string? name,
        string agentUrl,
        CancellationToken cancellationToken = default)
    {

        ArcanumSettings settings = _options.CurrentValue;

        ConclaveA2ASettings a2a = settings.Conclave.A2A ?? new ConclaveA2ASettings();

        if (!settings.Conclave.Enabled || !a2a.Enabled || !a2a.ClientEnabled)
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

        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.MaxTasksReached, $"The maximum number of concurrent external delegations ({a2a.MaxExternalTasks}) has been reached."));

        }

        try
        {

            return await DispatchInternalAsync(goal.Trim(), name, trimmedUrl, allowlist, a2a, cancellationToken)
                .ConfigureAwait(false);

        }
        finally
        {

            // Released here regardless of success, remote failure, or timeout, so a slow or hung remote
            // agent can never leak a concurrency slot past this call's own lifetime.
            gate.Release();

        }

    }

    private async Task<Result<A2ADispatchResult>> DispatchInternalAsync(
        string goal,
        string? name,
        string discoveryUrl,
        string[] allowlist,
        ConclaveA2ASettings a2a,
        CancellationToken cancellationToken)
    {

        int timeoutMinutes = ArcanumSettingClamps.ExternalTaskTimeoutMinutes(a2a.ExternalTaskTimeoutMinutes);

        using CancellationTokenSource timeoutCts = new(TimeSpan.FromMinutes(timeoutMinutes));

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        AgentCard card;

        try
        {

            card = await ResolveCardAsync(discoveryUrl, linkedCts.Token).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.TaskTimeout, $"Discovering the remote agent's Agent Card did not complete within {timeoutMinutes} minute(s)."));

        }
        catch (Exception ex) when (ex is HttpRequestException or A2AException or InvalidOperationException)
        {

            _logger.LogWarning(ex, "dispatch_sending: failed to resolve Agent Card at {AgentUrl}.", discoveryUrl);

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.AgentCardInvalid, $"Could not resolve the remote agent's Agent Card: {ex.Message}"));

        }

        string? interfaceUrl = card.SupportedInterfaces?.FirstOrDefault()?.Url;

        if (string.IsNullOrWhiteSpace(interfaceUrl))
        {

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.AgentCardInvalid, "The remote Agent Card did not advertise a usable interface."));

        }

        // The card's own interface URL is remote-controlled and may differ from the discovery URL; it must
        // pass the same allowlist and SSRF checks before this process connects to it.
        if (allowlist.Length > 0 && !IsAllowedAgent(interfaceUrl, allowlist))
        {

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.AgentNotAllowed, $"Remote agent interface '{interfaceUrl}' is not in the configured AllowedRemoteAgents allowlist."));

        }

        Result interfaceUrlCheck = await OutboundUrlGuard
            .ValidateUntrustedUrlAsync(interfaceUrl, linkedCts.Token)
            .ConfigureAwait(false);

        if (interfaceUrlCheck.IsFailure)
        {

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.AgentUnreachable, $"Remote agent interface rejected by outbound URL policy: {interfaceUrlCheck.Error.Message}"));

        }

        HttpClient httpClient = _httpClientFactory.CreateClient(OutboundHttpClientName);

        IA2AClient client = A2AClientFactory.Create(card, httpClient);

        Message message = new()
        {
            Role = Role.User,
            MessageId = Guid.NewGuid().ToString("N"),
            Parts = [Part.FromText(goal)],
        };

        SendMessageRequest sendRequest = new()
        {
            Message = message,
            Configuration = new SendMessageConfiguration { ReturnImmediately = false },
        };

        SendMessageResponse response;

        try
        {

            response = await client.SendMessageAsync(sendRequest, linkedCts.Token).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.TaskTimeout, $"The remote agent did not respond to '{name ?? "Sending"}' within {timeoutMinutes} minute(s)."));

        }
        catch (Exception ex) when (ex is HttpRequestException or A2AException)
        {

            _logger.LogWarning(ex, "dispatch_sending: failed to send message to {InterfaceUrl}.", interfaceUrl);

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.AgentUnreachable, $"Failed to send the Sending to the remote agent: {ex.Message}"));

        }

        if (response.PayloadCase == SendMessageResponseCase.Message)
        {

            return Result<A2ADispatchResult>.Success(new A2ADispatchResult(null, ExtractText(response.Message!.Parts)));

        }

        AgentTask task = response.Task
            ?? throw new InvalidOperationException("A2A SendMessageResponse carried neither a Message nor a Task payload.");

        // Blocking mode (ReturnImmediately = false) should already return a terminal task; poll defensively
        // in case the remote agent's binding does not honor the configuration hint.
        while (!TaskStateExtensions.IsTerminal(task.Status.State))
        {

            try
            {

                await Task.Delay(PollInterval, linkedCts.Token).ConfigureAwait(false);

                task = await client.GetTaskAsync(new GetTaskRequest { Id = task.Id }, linkedCts.Token).ConfigureAwait(false);

            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {

                return Result<A2ADispatchResult>.Failure(
                    new Error(ErrorCodes.Sending.TaskTimeout, $"The remote agent did not complete Sending '{task.Id}' within {timeoutMinutes} minute(s)."));

            }

        }

        if (task.Status.State == TaskState.Completed)
        {

            return Result<A2ADispatchResult>.Success(new A2ADispatchResult(task.Id, ExtractTaskText(task)));

        }

        string reason = task.Status.Message is { } statusMessage
            ? ExtractText(statusMessage.Parts)
            : $"Remote task ended in state {task.Status.State}.";

        return Result<A2ADispatchResult>.Failure(new Error(ErrorCodes.Sending.TaskRejected, reason));

    }

    private async Task<AgentCard> ResolveCardAsync(string discoveryUrl, CancellationToken cancellationToken)
    {

        if (_cardCache.TryGetValue(discoveryUrl, out CachedCard? cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {

            return cached.Card;

        }

        HttpClient httpClient = _httpClientFactory.CreateClient(OutboundHttpClientName);

        A2ACardResolver resolver = new(new Uri(discoveryUrl), httpClient);

        AgentCard card = await resolver.GetAgentCardAsync(cancellationToken).ConfigureAwait(false);

        _cardCache[discoveryUrl] = new CachedCard(card, DateTimeOffset.UtcNow.Add(CardCacheTtl));

        return card;

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
