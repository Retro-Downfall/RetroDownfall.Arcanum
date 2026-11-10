using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using A2A;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Host-resolved configuration; Card prose can never replace these destinations.</summary>
internal sealed record GuildConfiguredRemoteSource(string DiscoveryEndpoint, GuildRemoteTransportDescriptor Transport,
    string AdvertisedAgentKey = "default");

internal sealed record GuildRemoteCardObservation(GuildAgentCard Card, string Digest);

internal sealed class GuildRemoteCardReader(IDnsResolver dnsResolver)
{
    private const int MaximumCardBytes = 1024 * 1024;

    internal static Result<GuildRemoteCardObservation> Parse(GuildConfiguredRemoteSource source, ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumCardBytes)
        {
            return Failure(ErrorCodes.Guild.PayloadTooLarge);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);

            if (!UniqueProperties(document.RootElement))
            {
                return Failure(ErrorCodes.Guild.InvalidRequest);
            }

            AgentCard? card = JsonSerializer.Deserialize(bytes.Span,
                (JsonTypeInfo<AgentCard>)A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(AgentCard)));

            if (card is null || string.IsNullOrWhiteSpace(card.Name) || string.IsNullOrWhiteSpace(card.Description)
                || string.IsNullOrWhiteSpace(card.Version) || string.IsNullOrWhiteSpace(source.Transport.PeerId)
                || string.IsNullOrWhiteSpace(source.AdvertisedAgentKey))
            {
                return Failure(ErrorCodes.Guild.InvalidRequest);
            }

            if (card.SupportedInterfaces?.Any(item => item is null) == true
                || card.Skills?.Any(item => item is null) == true)
            {
                return Failure(ErrorCodes.Guild.InvalidRequest);
            }

            AgentInterface[] interfaces = [.. card.SupportedInterfaces ?? []];

            bool interfacesMatch = source.Transport.InterfaceObservations.IsDefault
                ? interfaces.Length > 0 && interfaces.All(item => ExactEndpoint(item.Url, source.Transport.Endpoint)
                    && source.Transport.Interfaces.Contains(item.ProtocolBinding, StringComparer.Ordinal)
                    && string.Equals(item.ProtocolVersion, source.Transport.ProtocolVersion, StringComparison.Ordinal))
                : interfaces.Select(item => new GuildRemoteInterfaceDescriptor(item.Url, item.ProtocolBinding, item.ProtocolVersion))
                    .SequenceEqual(source.Transport.InterfaceObservations);

            if (!interfacesMatch)
            {
                return Failure(ErrorCodes.Guild.DestinationChanged);
            }

            ImmutableArray<string> inputs = [.. card.DefaultInputModes ?? ["text/plain"]];

            ImmutableArray<string> outputs = [.. card.DefaultOutputModes ?? ["text/plain"]];

            ImmutableArray<GuildAgentSkillDto> skills = [.. (card.Skills ?? []).Select(skill => new GuildAgentSkillDto(
                skill.Id, skill.Name, skill.Description, [.. skill.Tags ?? []],
                skill.InputModes is { } skillInputs ? [.. skillInputs] : inputs, skill.OutputModes is { } skillOutputs ? [.. skillOutputs] : outputs))];

            if (skills.Any(skill => string.IsNullOrWhiteSpace(skill.Id) || string.IsNullOrWhiteSpace(skill.Name)
                    || string.IsNullOrWhiteSpace(skill.Description))
                || skills.Select(skill => skill.Id).Distinct(StringComparer.Ordinal).Count() != skills.Length)
            {
                return Failure(ErrorCodes.Guild.InvalidRequest);
            }

            GuildAgentCard projected = new(Guid.Empty, Guid.Empty, 0, card.Name, card.Description, card.Version,
                skills, inputs, outputs, new(card.Capabilities?.Streaming == true,
                    card.Capabilities?.PushNotifications == true, false),
                new(GuildAgentSourceKind.Remote, null, source.Transport.PeerId, true, null));

            return Result<GuildRemoteCardObservation>.Success(new(projected, Convert.ToHexString(SHA256.HashData(bytes.Span))));
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            return Failure(ErrorCodes.Guild.InvalidRequest);
        }
    }

    internal async Task<Result<GuildRemoteCardObservation>> ReadAsync(GuildConfiguredRemoteSource source, CancellationToken cancellationToken)
    {
        using CancellationTokenSource discovery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        discovery.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            Result discoveryAllowed = await OutboundUrlGuard.ValidateUntrustedUrlAsync(source.DiscoveryEndpoint,
                dnsResolver, discovery.Token).ConfigureAwait(false);

            Result interfaceAllowed = await OutboundUrlGuard.ValidateUntrustedUrlAsync(source.Transport.Endpoint,
                dnsResolver, discovery.Token).ConfigureAwait(false);

            if (discoveryAllowed.IsFailure || interfaceAllowed.IsFailure)
            {
                return Failure(ErrorCodes.Guild.Forbidden);
            }

            // Credential references cannot turn into environment/provider credentials without a qualified host adapter.
            if (source.Transport.CredentialReference is not null)
            {
                return Failure(ErrorCodes.Guild.AuthorityUnavailable);
            }

            using SocketsHttpHandler handler = OutboundUrlGuard.CreateUntrustedEgressHandler(dnsResolver, TimeSpan.FromSeconds(30));

            using HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };

            using HttpResponseMessage response = await client.GetAsync(source.DiscoveryEndpoint,
                HttpCompletionOption.ResponseHeadersRead, discovery.Token).ConfigureAwait(false);

            if (OutboundUrlGuard.IsRedirectStatusCode(response.StatusCode))
            {
                return Failure(ErrorCodes.Guild.DestinationChanged);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Failure(ErrorCodes.Guild.Unavailable);
            }

            if (response.Content.Headers.ContentLength is > MaximumCardBytes)
            {
                return Failure(ErrorCodes.Guild.PayloadTooLarge);
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(discovery.Token).ConfigureAwait(false);

            using MemoryStream buffer = new();

            byte[] chunk = new byte[16 * 1024];

            int read;

            while ((read = await body.ReadAsync(chunk, discovery.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaximumCardBytes)
                {
                    return Failure(ErrorCodes.Guild.PayloadTooLarge);
                }

                buffer.Write(chunk, 0, read);
            }

            return Parse(source, buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(ErrorCodes.Guild.Unavailable);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or ArgumentException)
        {
            return Failure(ErrorCodes.Guild.Unavailable);
        }
    }

    internal static string SourceIdentity(GuildConfiguredRemoteSource source) =>
        $"remote:{Convert.ToBase64String(Encoding.UTF8.GetBytes(source.Transport.PeerId))}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(source.AdvertisedAgentKey))}";

    private static bool ExactEndpoint(string? advertised, string configured) =>
        Uri.TryCreate(advertised, UriKind.Absolute, out Uri? observed)
        && Uri.TryCreate(configured, UriKind.Absolute, out Uri? expected)
        && string.Equals(observed.AbsoluteUri, expected.AbsoluteUri, StringComparison.Ordinal);

    private static bool UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || !UniqueProperties(property.Value))
                {
                    return false;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (!UniqueProperties(item))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static Result<GuildRemoteCardObservation> Failure(string code) =>
        Result<GuildRemoteCardObservation>.Failure(new Error(code, "The configured remote agent Card is unavailable or invalid."));
}
