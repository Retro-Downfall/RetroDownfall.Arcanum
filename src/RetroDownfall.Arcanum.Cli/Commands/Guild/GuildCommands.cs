using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Cli.Commands.Guild;

internal sealed record GuildTaskContentView(GuildQuestDto Quest, GuildContentDto Goal, GuildContentDto? Result);

internal sealed record GuildWatchRecord(string Event, string? Id, JsonElement Data);

internal sealed class GuildCliInputException(string message, int exitCode = 2) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

internal sealed partial class GuildCommands(
    ArcanumApiClient apiClient,
    IConsoleDispatcher dispatcher,
    GuildCliResourceCatalog guildCatalog,
    IOptions<ArcanumSettings> settings)
{
    private const int InputLimit = 65_536;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<int> ExecuteAsync(string path, GuildCommandInput input, CancellationToken cancellationToken)
    {
        try
        {
            Validate(input);

            if (path.StartsWith("watch ", StringComparison.Ordinal))
            {
                return await WatchAsync(path, input, cancellationToken).ConfigureAwait(false);
            }

            return await ExecuteFiniteAsync(path, input, cancellationToken).ConfigureAwait(false);
        }
        catch (GuildCliInputException exception)
        {
            return Failure(exception.Message, exception.ExitCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("Guild operation cancelled.", 130);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or EncoderFallbackException)
        {
            return Failure("Guild input could not be read as bounded UTF-8 or valid typed JSON: " + exception.Message, 2);
        }
    }

    private async Task<int> ExecuteFiniteAsync(string path, GuildCommandInput input, CancellationToken ct)
    {
        Guid key = input.Text("idempotency-key") is { } requestedKey ? ExactGuid(requestedKey, "--idempotency-key") : Guid.NewGuid();

        long revision = input.Number("expected-revision") ?? 0;

        string? cursor = input.Text("cursor");

        int pageSize = (int)(input.Number("page-size") ?? 100);

        switch (path)
        {
            case "status": return await Get<GuildStatusDto>("api/guild/status", ct).ConfigureAwait(false);

            case "agent list":
                return await Get<GuildAgentCardPageDto>(Query("api/guild/agents", cursor, pageSize,
                    ("source", input.Text("source")), ("skill", input.Text("skill"))), ct).ConfigureAwait(false);

            case "agent show":
            {
                Guid agent = await Select("agent", input.Argument(), ct).ConfigureAwait(false);

                return await Get<GuildAgentCardDto>(ArcanumApiClient.GuildQuery($"api/guild/agents/{agent:D}",
                    ("cardRevision", Number(input.Number("card-revision")))), ct).ConfigureAwait(false);
            }

            case "agent refresh":
            {
                if (input.Argument() is not null && cursor is not null) throw new GuildCliInputException("An Agent selector and --cursor are mutually exclusive.");

                Guid? agent = input.Argument() is null ? null : await Select("agent", input.Argument(), ct).ConfigureAwait(false);

                return await Post<GuildRefreshAgentsRequest, GuildRefreshReceipt>("api/guild/agents/refresh", new(key, agent, cursor), ct).ConfigureAwait(false);
            }

            case "task create":
            {
                string goal = await Text(input, "goal", ct).ConfigureAwait(false);

                GuildGrant grant = await Grant<GuildGrant>(input.Text("grant-file"), required: true, ct).ConfigureAwait(false)
                    ?? throw new GuildCliInputException("A complete --grant-file is required.");

                Guid? agent = await Optional("agent", input.Text("agent"), ct).ConfigureAwait(false);

                Guid? session = await ExistingResource("session", input.Text("session"), ct).ConfigureAwait(false);

                Guid? campaign = await ExistingResource("campaign", input.Text("campaign"), ct).ConfigureAwait(false);

                Guid? workspace = await ExistingResource("workspace", input.Text("workspace"), ct).ConfigureAwait(false);

                Guid? predecessor = await Optional("task", input.Text("predecessor"), ct).ConfigureAwait(false);

                return await Post<GuildIssueRootRequest, GuildQuestDto>("api/guild/quests", new(key, agent, input.Text("name"), goal,
                    grant, session, campaign, workspace, predecessor), ct).ConfigureAwait(false);
            }

            case "task delegate":
            {
                string goal = await Text(input, "goal", ct).ConfigureAwait(false);

                GuildRequestedGrant? grant = await Grant<GuildRequestedGrant>(input.Text("grant-file"), false, ct).ConfigureAwait(false);

                Guid parent = await Select("task", input.Argument(), ct).ConfigureAwait(false);

                Guid? agent = await Optional("agent", input.Text("agent"), ct).ConfigureAwait(false);

                return await Post<GuildIssueChildRequest, GuildQuestDto>($"api/guild/quests/{parent:D}/children",
                    new(key, revision, agent, input.Text("name"), goal, grant), ct).ConfigureAwait(false);
            }

            case "task list":
            {
                Guid? root = await Optional("task", input.Text("root"), ct).ConfigureAwait(false);

                Guid? parent = await Optional("task", input.Text("parent"), ct).ConfigureAwait(false);

                return await Get<GuildQuestPageDto>(Query("api/guild/quests", cursor, pageSize,
                    ("rootId", Id(root)), ("parentId", Id(parent)), ("state", input.Text("state"))), ct).ConfigureAwait(false);
            }

            case "task show":
            {
                Guid quest = await Select("task", input.Argument(), ct).ConfigureAwait(false);

                Result<GuildQuestDto> result = await Read<GuildQuestDto>($"api/guild/quests/{quest:D}", ct).ConfigureAwait(false);

                if (result.IsFailure) return Failure(result.Error);

                if (!input.Flag("show-content")) return Output(result.Value);

                Result<GuildContentDto> goal = await Read<GuildContentDto>(ContentRoute(result.Value.GoalRef), ct).ConfigureAwait(false);

                if (goal.IsFailure) return Failure(goal.Error);

                GuildContentDto? body = null;

                if (result.Value.ResultRef is { } reference)
                {
                    Result<GuildContentDto> content = await Read<GuildContentDto>(ContentRoute(reference), ct).ConfigureAwait(false);

                    if (content.IsFailure) return Failure(content.Error);

                    body = content.Value;
                }

                return Output(new GuildTaskContentView(result.Value, goal.Value, body), CliJsonContext.Default.GuildTaskContentView);
            }

            case "task tree":
            {
                Guid quest = await Select("task", input.Argument(), ct).ConfigureAwait(false);

                return await Get<GuildQuestPageDto>(Query($"api/guild/quests/{quest:D}/tree", cursor, pageSize), ct).ConfigureAwait(false);
            }

            case "task message":
            {
                string text = await Text(input, "text", ct).ConfigureAwait(false);

                Guid quest = await Select("task", input.Argument(), ct).ConfigureAwait(false);

                return await Post<GuildSendOperatorMessageRequest, GuildMessageReceipt>($"api/guild/quests/{quest:D}/messages",
                    new(key, revision, input.Flag("continue") ? GuildMessageKind.Continuation : GuildMessageKind.Message, "text/plain", text), ct).ConfigureAwait(false);
            }

            case "task cancel":
            {
                Guid quest = await Select("task", input.Argument(), ct).ConfigureAwait(false);

                return await Post<GuildCancelQuestRequest, GuildCancellationDto>($"api/guild/quests/{quest:D}/cancel",
                    new(key, revision, GuildCancellationReason.OperatorRequested), ct).ConfigureAwait(false);
            }

            case "task spend":
            {
                Guid quest = await Select("task", input.Argument(), ct).ConfigureAwait(false);

                return await Get<GuildSpendDto>($"api/guild/quests/{quest:D}/spend", ct).ConfigureAwait(false);
            }

            case "signet revoke":
            {
                Guid signet = await Select("signet", input.Argument(), ct).ConfigureAwait(false);

                return await Post<GuildRevokeSignetRequest, SignetRevocationDto>($"api/guild/signets/{signet:D}/revoke",
                    new(key, revision, GuildCancellationReason.OperatorRequested), ct).ConfigureAwait(false);
            }

            case "content show":
            {
                Guid content = await Select("content", input.Argument(), ct).ConfigureAwait(false);

                return await Get<GuildContentDto>(ArcanumApiClient.GuildQuery($"api/guild/content/{content:D}",
                    ("revision", Number(input.Number("revision")))), ct).ConfigureAwait(false);
            }

            default: return await CohortAsync(path, input, key, revision, ct).ConfigureAwait(false);
        }
    }

    private static void Validate(GuildCommandInput input)
    {
        foreach (string name in new[] { "expected-revision", "expected-cohort-revision", "revision", "card-revision", "epoch", "quorum" })
        {
            if (input.Number(name) is <= 0) throw new GuildCliInputException("--" + name + " must be positive.");
        }

        if (input.Number("page-size") is < 1 or > 100) throw new GuildCliInputException("--page-size must be between 1 and 100.");

        foreach (string name in new[] { "goal", "text" })
        {
            if (input.Text(name) is not null && input.Text(name + "-file") is not null)
                throw new GuildCliInputException("--" + name + " and --" + name + "-file are mutually exclusive.");
        }

        if (input.Flag("ndjson") && CliInvocationContext.Current.Json)
            throw new GuildCliInputException("--ndjson and --json are mutually exclusive.");

        if (input.Text("after") is not null && CliInvocationContext.Current.Json)
            throw new GuildCliInputException("--after cannot be used with --json; request a fresh snapshot or use --ndjson.");
    }

    private async Task<Guid> Select(string kind, string? value, CancellationToken ct, Guid? scope = null)
    {
        ResourceSelectionResult<GuildResourceChoice> result = await guildCatalog.SelectAsync(kind, value, scope, ct).ConfigureAwait(false);

        if (result.Status == ResourceSelectionStatus.Cancelled) throw new GuildCliInputException("Guild selection cancelled.", 130);

        if (result.Status == ResourceSelectionStatus.Error)
            throw new GuildCliInputException(result.Error!, result.ErrorCode is null ? 2 : CliFailureExit.ExitCode(result.ErrorCode));

        return result.Value!.Id;
    }

    private async Task<Guid?> Optional(string kind, string? value, CancellationToken ct) =>
        value is null ? null : await Select(kind, value, ct).ConfigureAwait(false);

    private async Task<Guid?> ExistingResource(string kind, string? value, CancellationToken ct) =>
        value is null ? null : await Select(kind, value, ct).ConfigureAwait(false);

    private static Guid ExactGuid(string value, string name) => Guid.TryParseExact(value, "D", out Guid id) && id != Guid.Empty
        ? id : throw new GuildCliInputException(name + " must be a nonempty UUID.");

    private static async Task<string> Text(GuildCommandInput input, string name, CancellationToken ct)
    {
        string? text = input.Text(name);

        if (input.Text(name + "-file") is { } file) text = await ReadInput(file, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(text)) throw new GuildCliInputException("--" + name + " or --" + name + "-file is required and must not be empty.");

        if (StrictUtf8.GetByteCount(text) > InputLimit) throw new GuildCliInputException("--" + name + " exceeds the 65536-byte representation limit.");

        return text;
    }

    private static async Task<T?> Grant<T>(string? path, bool required, CancellationToken ct) where T : class
    {
        if (path is null)
        {
            if (required) throw new GuildCliInputException("--grant-file is required.");

            return null;
        }

        string json = await ReadInput(path, ct).ConfigureAwait(false);

        return JsonSerializer.Deserialize(json, Data<T>()) ?? throw new GuildCliInputException("The grant file must contain a JSON object.");
    }

    private static async Task<string> ReadInput(string path, CancellationToken ct)
    {
        if (path == "-")
        {
            char[] buffer = new char[InputLimit + 1];

            int count = 0;

            while (count < buffer.Length)
            {
                int read = await Console.In.ReadAsync(buffer.AsMemory(count), ct).ConfigureAwait(false);

                if (read == 0) break;

                count += read;
            }

            string text = new(buffer, 0, count);

            if (StrictUtf8.GetByteCount(text) > InputLimit) throw new GuildCliInputException("stdin exceeds the 65536-byte representation limit.");

            return text;
        }

        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);

        byte[] bytes = new byte[InputLimit + 1];

        int length = 0;

        while (length < bytes.Length)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(length), ct).ConfigureAwait(false);

            if (read == 0) break;

            length += read;
        }

        if (length > InputLimit) throw new GuildCliInputException("The input file exceeds the 65536-byte representation limit.");

        int start = length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;

        return StrictUtf8.GetString(bytes, start, length - start);
    }

    private async Task<int> Get<T>(string path, CancellationToken ct)
    {
        Result<T> result = await Read<T>(path, ct).ConfigureAwait(false);

        return result.IsFailure ? Failure(result.Error) : Output(result.Value);
    }

    private Task<Result<T>> Read<T>(string path, CancellationToken ct) => apiClient.GetGuildAsync(path, Envelope<T>(), ct);

    private async Task<int> Post<TRequest, T>(string path, TRequest request, CancellationToken ct)
    {
        Result<T> result = await apiClient.PostGuildAsync(path, request, Data<TRequest>(), Envelope<T>(), ct).ConfigureAwait(false);

        return result.IsFailure ? Failure(result.Error) : Output(result.Value);
    }

    private int Output<T>(T value) => Output(value, Data<T>());

    private int Output<T>(T value, JsonTypeInfo<T> metadata)
    {
        if (CliInvocationContext.Current.Json) dispatcher.WriteJson(value, metadata);
        else dispatcher.WritePayload(JsonSerializer.Serialize(value, metadata));

        return 0;
    }

    private int Failure(Error error) => Failure(CliFailureExit.Annotate(error, settings.Value.Host).Message, CliFailureExit.ExitCode(error));

    private int Failure(string message, int exitCode)
    {
        dispatcher.WriteDiagnostic(message);

        if (CliInvocationContext.Current.Json)
            dispatcher.WriteJson(new CliErrorPayload(message, exitCode), CliJsonContext.Default.CliErrorPayload);

        return exitCode;
    }

    // These lookups resolve only entries in the generated metadata graph; no runtime type inference
    // or reflection serialization is used for any input, output or transport payload.
    private static JsonTypeInfo<T> Data<T>() => (JsonTypeInfo<T>)(GuildJsonContext.Default.GetTypeInfo(typeof(T))
        ?? throw new InvalidOperationException("The Guild generated payload metadata is unavailable."));

    private static JsonTypeInfo<ApiResponse<T>> Envelope<T>() => (JsonTypeInfo<ApiResponse<T>>)(ArcanumJsonContext.Default.GetTypeInfo(typeof(ApiResponse<T>))
        ?? throw new InvalidOperationException("The Guild generated envelope metadata is unavailable."));

    private static string? Number(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Id(Guid? value) => value?.ToString("D");

    private static string Query(string path, string? cursor, int pageSize, params (string Name, string? Value)[] filters) =>
        ArcanumApiClient.GuildQuery(path, [("cursor", cursor), ("pageSize", pageSize.ToString(CultureInfo.InvariantCulture)), .. filters]);

    private static string ContentRoute(GuildDataRef reference) => ArcanumApiClient.GuildQuery($"api/guild/content/{reference.ContentId:D}", ("revision", Number(reference.Revision)));
}
