using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Cli.Commands.Guild;

internal sealed partial class GuildCommands
{
    private async Task<int> SpendAsync(string path, GuildCommandInput input, CancellationToken ct)
    {
        Guid? effect = input.Text("effect") is { } selectedEffect ? ExactGuid(selectedEffect, "--effect") : null;

        Guid? receipt = input.Text("receipt") is { } selectedReceipt ? ExactGuid(selectedReceipt, "--receipt") : null;

        GuildSpendCorrectionRequest? correction = null;

        if (path == "spend correct")
        {
            Guid key = ExactGuid(input.Text("idempotency-key") ?? "", "--idempotency-key");

            long revision = input.Number("expected-revision") ?? throw new GuildCliInputException("--expected-revision is required.");

            long Tokens(string name) => input.Number(name) is >= 0 and var value ? value
                : throw new GuildCliInputException("--" + name + " must be an explicit nonnegative integer.");

            string rawUsd = input.Text("usd") ?? throw new GuildCliInputException("--usd is required.");

            using JsonDocument json = JsonDocument.Parse(rawUsd);

            if (json.RootElement.ValueKind != JsonValueKind.Number) throw new GuildCliInputException("--usd must be an exact nonnegative JSON number.");

            GuildSpendAmountsRequest amounts = JsonSerializer.Deserialize("{\"usd\":" + json.RootElement.GetRawText() + "}", GuildJsonContext.Default.GuildSpendAmountsRequest)
                ?? throw new GuildCliInputException("--usd must be an exact nonnegative JSON number.");

            if (amounts.Usd is null or < 0) throw new GuildCliInputException("--usd must be an exact nonnegative JSON number.");

            string digest = input.Text("evidence-digest") ?? "";

            if (digest.Length != 64 || !digest.All(Uri.IsHexDigit)) throw new GuildCliInputException("--evidence-digest must be 64 hexadecimal characters.");

            correction = new(key, revision, GuildSpendOrigin.OperatorAttested,
                new(Tokens("input-tokens"), Tokens("output-tokens"), Tokens("cached-tokens"), Tokens("reasoning-tokens"), amounts.Usd), digest);
        }

        GuildSpendRefreshRequest? refresh = path == "spend refresh"
            ? new(ExactGuid(input.Text("idempotency-key") ?? "", "--idempotency-key")) : null;

        Guid quest = await Select("task", input.Argument(), ct).ConfigureAwait(false);

        string root = $"api/guild/quests/{quest:D}/spend/effects";

        int pageSize = (int)(input.Number("page-size") ?? 100);

        return path switch
        {
            "spend list" => await Get<GuildSpendEffectPageDto>(Query(root, input.Text("cursor"), pageSize), ct).ConfigureAwait(false),
            "spend show" => await Get<GuildSpendEffectDto>($"{root}/{effect:D}", ct).ConfigureAwait(false),
            "spend correct" => await Post<GuildSpendCorrectionRequest, GuildSpendCorrectionResultDto>($"{root}/{effect:D}/corrections", correction!, ct).ConfigureAwait(false),
            "spend refresh" => await RefreshSpendAsync($"{root}/{effect:D}/refreshes", refresh!, ct).ConfigureAwait(false),
            "spend receipts" => await Get<GuildSpendReceiptPageDto>(Query($"{root}/{effect:D}/receipts", input.Text("cursor"), pageSize), ct).ConfigureAwait(false),
            "spend receipt" => await Get<GuildSpendReceiptDto>($"{root}/{effect:D}/receipts/{receipt:D}", ct).ConfigureAwait(false),
            _ => throw new GuildCliInputException("Unknown spend command."),
        };
    }

    private async Task<int> RefreshSpendAsync(string path, GuildSpendRefreshRequest request, CancellationToken ct)
    {
        var result = await apiClient.PostGuildAsync(path, request, Data<GuildSpendRefreshRequest>(),
            Envelope<GuildSpendRefreshResultDto>(), ct).ConfigureAwait(false);

        if (result.IsFailure) return Failure(result.Error);

        _ = Output(result.Value);

        return result.Value.InFlight || result.Value.RequiresAttention ? 1 : 0;
    }
}
