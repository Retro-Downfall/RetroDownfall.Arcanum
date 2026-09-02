using System.Text.Json.Nodes;
using System.Threading.Channels;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

/// <summary>
/// A wire-level MCP server over the same newline-delimited channel pair the in-process client transport
/// uses. Its one tool raises <c>elicitation/create</c> against the client while the tool call is in
/// flight and reports the client's answer as the tool result, so a test can prove what the client's
/// elicitation handler actually did. Every wait is bounded so a wrong answer reds instead of hanging.
/// </summary>
internal sealed class FakeElicitingMcpServer : IAsyncDisposable
{

    internal const string ToolName = "elicit_answer";

    internal const string Question = "What is the answer?";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly Channel<string> _toServer = Channel.CreateUnbounded<string>();

    private readonly Channel<string> _fromServer = Channel.CreateUnbounded<string>();

    private readonly CancellationTokenSource _lifetime = new();

    private readonly Lock _sync = new();

    private readonly List<string> _outcomes = [];

    private readonly Task _loop;

    private int _elicitations;

    public FakeElicitingMcpServer()
    {
        _loop = Task.Run(() => RunAsync(_lifetime.Token));
    }

    /// <summary>Each elicitation the client answered, in order: <c>accept:value</c> or <c>decline:reason</c>.</summary>
    public IReadOnlyList<string> ElicitationOutcomes
    {
        get
        {
            lock (_sync)
            {
                return [.. _outcomes];
            }
        }
    }

    public ChannelClientTransport CreateClientTransport() =>
        new(_toServer.Writer, _fromServer.Reader, maxJsonRpcLineBytes: 1_048_576);

    public async ValueTask DisposeAsync()
    {

        await _lifetime.CancelAsync();

        _fromServer.Writer.TryComplete();

        _toServer.Writer.TryComplete();

        try
        {
            await _loop.WaitAsync(Bound);
        }
        catch (Exception)
        {
            // A loop that will not stop is reported by the test that observed it, not by disposal.
        }

        _lifetime.Dispose();

    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                JsonObject? message = await ReadMessageAsync(cancellationToken);

                if (message is null)
                {
                    return;
                }

                await HandleAsync(message, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal or a bounded wait that expired.
        }
        catch (ChannelClosedException)
        {
            // The client completed its side of the channel.
        }
        finally
        {
            _fromServer.Writer.TryComplete();
        }
    }

    private async Task<JsonObject?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        while (await _toServer.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_toServer.Reader.TryRead(out string? line))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (JsonNode.Parse(line) is JsonObject message)
                {
                    return message;
                }
            }
        }

        return null;
    }

    private async Task HandleAsync(JsonObject message, CancellationToken cancellationToken)
    {
        string? method = message["method"]?.GetValue<string>();

        JsonNode? id = message["id"];

        if (method is null || id is null)
        {
            return;
        }

        switch (method)
        {
            case "initialize":
                string protocolVersion = message["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18";

                await RespondAsync(
                    id,
                    new JsonObject
                    {
                        ["protocolVersion"] = protocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "fake-eliciting-server", ["version"] = "1.0.0" },
                    },
                    cancellationToken);

                break;

            case "ping":
                await RespondAsync(id, new JsonObject(), cancellationToken);

                break;

            case "tools/list":
                await RespondAsync(
                    id,
                    new JsonObject
                    {
                        ["tools"] = new JsonArray(
                            new JsonObject
                            {
                                ["name"] = ToolName,
                                ["description"] = "asks the operator a question through MCP elicitation",
                                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                            }),
                    },
                    cancellationToken);

                break;

            case "tools/call":
                await HandleToolCallAsync(id, cancellationToken);

                break;

            default:
                await WriteAsync(
                    new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id.DeepClone(),
                        ["error"] = new JsonObject
                        {
                            ["code"] = -32601,
                            ["message"] = $"Method '{method}' is not supported by the fake server.",
                        },
                    },
                    cancellationToken);

                break;
        }
    }

    private async Task HandleToolCallAsync(JsonNode callId, CancellationToken cancellationToken)
    {
        string elicitationId = $"elicitation-{Interlocked.Increment(ref _elicitations)}";

        await WriteAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = elicitationId,
                ["method"] = "elicitation/create",
                ["params"] = new JsonObject
                {
                    ["message"] = Question,
                    ["requestedSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["answer"] = new JsonObject { ["type"] = "string" } },
                        ["required"] = new JsonArray("answer"),
                    },
                },
            },
            cancellationToken);

        JsonObject response = await ReadResponseAsync(elicitationId, cancellationToken);

        JsonObject? result = response["result"] as JsonObject;

        string action = result?["action"]?.GetValue<string>() ?? "error";

        JsonObject? content = result?["content"] as JsonObject;

        string outcome = action switch
        {
            "accept" => $"accept:{content?["answer"]?.GetValue<string>()}",
            "decline" => $"decline:{content?["reason"]?.GetValue<string>()}",
            _ => $"{action}:{response["error"]?["message"]?.GetValue<string>()}",
        };

        lock (_sync)
        {
            _outcomes.Add(outcome);
        }

        await RespondAsync(
            callId,
            new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"elicitation:{outcome}" }),
                ["isError"] = false,
            },
            cancellationToken);
    }

    private async Task<JsonObject> ReadResponseAsync(string id, CancellationToken cancellationToken)
    {
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        bound.CancelAfter(Bound);

        string expectedId = JsonValue.Create(id).ToJsonString();

        while (true)
        {
            JsonObject message = await ReadMessageAsync(bound.Token)
                ?? throw new InvalidOperationException($"The client closed the channel before answering '{id}'.");

            if (message["method"] is null)
            {
                if (string.Equals(message["id"]?.ToJsonString(), expectedId, StringComparison.Ordinal))
                {
                    return message;
                }

                continue;
            }

            await HandleAsync(message, cancellationToken);
        }
    }

    private Task RespondAsync(JsonNode id, JsonObject result, CancellationToken cancellationToken) =>
        WriteAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.DeepClone(),
                ["result"] = result,
            },
            cancellationToken);

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken) =>
        await _fromServer.Writer.WriteAsync(message.ToJsonString(), cancellationToken);

}
