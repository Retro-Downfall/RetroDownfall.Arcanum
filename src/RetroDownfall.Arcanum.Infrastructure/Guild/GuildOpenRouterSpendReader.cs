using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildSpendObservation(GuildSpendAmountsRequest Amounts, string EvidenceDigest);

/// <summary>One joined fixed-origin attempt. The caller retains its physical lease until this method returns.</summary>
internal sealed class GuildOpenRouterSpendReader(IDnsResolver dns, Func<HttpMessageHandler>? transport = null)
{
    private const int MaximumBytes = 64 * 1024;

    internal async Task<Result<GuildSpendObservation>> ReadAsync(GuildSpendSourceCredential.VerifiedSource source, CancellationToken ct)
    {
        using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);

        attempt.CancelAfter(TimeSpan.FromSeconds(30));

        string url = "https://openrouter.ai/api/v1/generation?id=" + Uri.EscapeDataString(source.GenerationId);

        try
        {
            if ((await OutboundUrlGuard.ValidateUntrustedUrlAsync(url, dns, attempt.Token).ConfigureAwait(false)).IsFailure)
                return Unavailable;

            using HttpMessageHandler handler = transport?.Invoke() ?? OutboundUrlGuard.CreateUntrustedEgressHandler(dns, TimeSpan.FromSeconds(30));

            using HttpClient client = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

            using HttpResponseMessage response = await source.SendAsync(client, attempt.Token).ConfigureAwait(false);

            attempt.Token.ThrowIfCancellationRequested();

            if (!response.IsSuccessStatusCode || OutboundUrlGuard.IsRedirectStatusCode(response.StatusCode)) return Unavailable;

            if (response.Content.Headers.ContentLength is > MaximumBytes) return Unavailable;

            await using Stream stream = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);

            byte[] bytes = new byte[MaximumBytes];

            int length = 0;

            while (length < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(length), attempt.Token).ConfigureAwait(false);

                attempt.Token.ThrowIfCancellationRequested();

                if (read == 0) break;

                length += read;
            }

            if (length == bytes.Length && await stream.ReadAsync(bytes.AsMemory(0, 1), attempt.Token).ConfigureAwait(false) != 0)
                return Unavailable;

            attempt.Token.ThrowIfCancellationRequested();

            Result<GuildSpendAmountsRequest> parsed = GuildOpenRouterSpendParser.Parse(bytes.AsMemory(0, length), source.GenerationId, source.Model);

            if (parsed.IsFailure) return Unavailable;

            GuildSpendAmountsRequest amounts = parsed.Value;

            string canonical = string.Join("\n", "Arcanum.Guild.OpenRouterGeneration.Facts.v1",
                amounts.InputTokens!.Value.ToString(CultureInfo.InvariantCulture),
                amounts.OutputTokens!.Value.ToString(CultureInfo.InvariantCulture),
                amounts.CachedTokens!.Value.ToString(CultureInfo.InvariantCulture),
                amounts.ReasoningTokens!.Value.ToString(CultureInfo.InvariantCulture),
                amounts.Usd!.Value.ToString("G29", CultureInfo.InvariantCulture));

            return new GuildSpendObservation(amounts, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Unavailable;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or ArgumentException or FormatException)
        {
            return Unavailable;
        }
    }

    private static Error Unavailable => new(ErrorCodes.Guild.Unavailable, "The provider spend observation is unavailable.");
}
