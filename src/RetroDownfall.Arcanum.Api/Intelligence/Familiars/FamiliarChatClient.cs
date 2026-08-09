using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Familiars;

namespace RetroDownfall.Arcanum.Api.Intelligence.Familiars;

/// <summary>
/// A Familiar invocation that did not produce a usable completion. Distinct from a generic
/// exception so the turn pipeline can tell a transport problem apart from a model refusal and
/// classify fallback correctly, and so the operator sees the CLI's own remediation instead of a
/// stack trace.
/// </summary>
public sealed class FamiliarTransportException(
    string message,
    FamiliarProcessFailure failure = FamiliarProcessFailure.None,
    Exception? innerException = null) : Exception(message, innerException)
{

    public FamiliarProcessFailure Failure { get; } = failure;

}

/// <summary>
/// Shared spine for the Familiar <see cref="IChatClient"/> adapters: one child process per inference
/// turn, NDJSON in, <see cref="ChatResponseUpdate"/> out.
/// </summary>
/// <remarks>
/// <para>
/// Everything above <c>IChatClientFactory</c> is untouched by this transport, which only works
/// because the adapter is an ordinary <see cref="IChatClient"/>. That has two consequences worth
/// stating plainly.
/// </para>
/// <para>
/// First, most of <c>ChatOptions</c> has nowhere to go. <c>Tools</c> is ignored on purpose: Arcanum
/// runs its own tool loop, and handing the tool set to the CLI would mean delegating into that CLI's
/// agent loop — an explicit non-goal, and a much larger design decision than a transport. A Familiar
/// therefore serves text completions, and a tool-using turn degrades to text rather than failing.
/// <c>ResponseFormat</c> <em>is</em> honoured, because both CLIs expose a structured-output flag and
/// silently dropping a schema would fail validation downstream instead of at the transport. Sampling
/// controls (<c>Temperature</c>, <c>TopP</c>, <c>MaxOutputTokens</c>, penalties, <c>Seed</c>,
/// <c>StopSequences</c>) have no headless equivalent in either CLI and are not applied — see
/// DESIGN §16.1.
/// </para>
/// <para>
/// Second, the process lifetime is the lease lifetime. <c>ChatClientLease</c> already owns a fresh
/// client per turn, so there is no long-lived CLI to leak, wedge, or reason about across turns.
/// </para>
/// </remarks>
internal abstract class FamiliarChatClient(
    IFamiliarProcessRunner runner,
    ProviderSettings provider,
    string resolvedModel,
    IReadOnlyList<string> deniedEnvironmentVariables) : IChatClient
{

    private readonly FamiliarWorkingDirectory _workingDirectory = FamiliarWorkingDirectory.Create();

    protected ProviderSettings Provider { get; } = provider;

    protected string ResolvedModel { get; } = resolvedModel;

    /// <summary>
    /// Where this turn's Familiar runs. Never the host's current directory: both CLIs read project
    /// state — Claude Code's <c>.claude/settings.json</c> hooks, Codex's <c>AGENTS.md</c> and
    /// <c>.rules</c> — out of their working root, so inheriting it would let whatever repository
    /// Arcanum was started in steer or execute code on every turn.
    /// </summary>
    protected string WorkingDirectory => _workingDirectory.Path;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {

        ChatResponse response = await GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToChatResponseAsync(cancellationToken)
            .ConfigureAwait(false);

        response.ModelId = ResolvedModel;

        return response;

    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(messages);

        FamiliarPrompt prompt = FamiliarPromptComposer.Compose(messages);

        FamiliarProcessRequest request = BuildRequest(prompt, ResolveJsonSchema(options)) with
        {
            DeniedEnvironmentVariables = deniedEnvironmentVariables,
        };

        FamiliarTurnState state = new();

        IAsyncEnumerator<string> lines = runner
            .RunLinesAsync(request, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {

            while (true)
            {

                bool moved;

                try
                {

                    moved = await lines.MoveNextAsync().ConfigureAwait(false);

                }
                catch (FamiliarProcessException ex)
                {

                    throw new FamiliarTransportException(
                        $"Provider '{Provider.Name}': {ex.Message}",
                        ex.Failure,
                        ex);

                }

                if (!moved)
                {
                    break;
                }

                foreach (ChatResponseUpdate update in ProjectFrame(lines.Current, state))
                {
                    yield return update;
                }

            }

        }
        finally
        {

            await lines.DisposeAsync().ConfigureAwait(false);

        }

        // A Familiar that produced no terminal frame did not finish. Yielding what arrived would
        // present a truncated stream as a short answer, which is exactly the silent-empty-completion
        // failure mode this transport must not have. The CLI's own last words, when it left any, say
        // more than a generic message can.
        if (!state.Completed)
        {

            throw new FamiliarTransportException(
                state.Diagnostic is { Length: > 0 } diagnostic
                    ? $"Provider '{Provider.Name}' ended without completing the turn: {diagnostic.Trim()}"
                    : $"Provider '{Provider.Name}' ended without completing the turn. The {FamiliarProviders.DisplayName(Provider.Type)} produced no terminal frame — re-run with the CLI directly to see its output.");

        }

        // A terminal frame with nothing in it is a completed turn with no answer. Left alone it
        // would reach the operator as an empty assistant message, which is the same silent failure
        // wearing a success label.
        if (!state.EmittedText && string.IsNullOrEmpty(state.CompleteText))
        {

            throw new FamiliarTransportException(
                state.Diagnostic is { Length: > 0 } diagnostic
                    ? $"Provider '{Provider.Name}' completed the turn without an answer: {diagnostic.Trim()}"
                    : $"Provider '{Provider.Name}' completed the turn without an answer.");

        }

        foreach (ChatResponseUpdate update in Finish(state))
        {
            yield return update;
        }

    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => _workingDirectory.Dispose();

    /// <summary>The exact invocation for this CLI: binary, argument list, and prompt on stdin.</summary>
    /// <param name="prompt">System instructions and the flattened conversation.</param>
    /// <param name="jsonSchema">
    /// The structured-output schema when the caller asked for one, already serialized. Null for an
    /// ordinary text turn.
    /// </param>
    protected abstract FamiliarProcessRequest BuildRequest(FamiliarPrompt prompt, string? jsonSchema);

    /// <summary>
    /// The JSON schema the caller asked the model to produce, if any. Arcanum validates structured
    /// output and retries on a mismatch, so dropping the schema here would spend extra turns getting
    /// prose rejected — both CLIs can be told the shape, so they are.
    /// </summary>
    private static string? ResolveJsonSchema(ChatOptions? options) =>
        options?.ResponseFormat is ChatResponseFormatJson { Schema: { } schema }
            ? schema.GetRawText()
            : null;

    /// <summary>Maps one NDJSON line into zero or more updates, recording terminal state.</summary>
    protected abstract IEnumerable<ChatResponseUpdate> ProjectFrame(string line, FamiliarTurnState state);

    /// <summary>
    /// Emits whatever the terminal frame implied: the complete text when no deltas arrived, and the
    /// provider-reported usage.
    /// </summary>
    private IEnumerable<ChatResponseUpdate> Finish(FamiliarTurnState state)
    {

        if (!state.EmittedText && !string.IsNullOrEmpty(state.CompleteText))
        {

            yield return Update([new TextContent(state.CompleteText)]);

        }

        if (state.Usage is not null)
        {

            yield return Update([new UsageContent(state.Usage)]);

        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
        {

            ModelId = ResolvedModel,

            FinishReason = ChatFinishReason.Stop,

        };

    }

    protected ChatResponseUpdate Update(IList<AIContent> contents) =>
        new(ChatRole.Assistant, contents) { ModelId = ResolvedModel };

    /// <summary>
    /// Reports a failure the CLI described in its own words. The vendor knows why it refused and
    /// Arcanum does not, so its message is carried through rather than replaced with a generic one.
    /// </summary>
    protected FamiliarTransportException Refused(string? detail) =>
        new(
            string.IsNullOrWhiteSpace(detail)
                ? $"Provider '{Provider.Name}' rejected the turn and gave no reason."
                : $"Provider '{Provider.Name}': {detail.Trim()}");

}

/// <summary>Accumulated state for one Familiar turn, threaded through frame projection.</summary>
internal sealed class FamiliarTurnState
{

    /// <summary>Whether a terminal frame was seen — the only proof the turn actually finished.</summary>
    public bool Completed { get; set; }

    /// <summary>Whether any text delta was already projected, so the fallback stays a fallback.</summary>
    public bool EmittedText { get; set; }

    /// <summary>The complete answer from the terminal frame, used when no deltas arrived.</summary>
    public string? CompleteText { get; set; }

    /// <summary>
    /// The CLI's own words about something that went wrong but did not end the turn. Kept apart from
    /// <see cref="CompleteText"/> so a complaint can never be handed back as the assistant's answer,
    /// while still being available to explain a turn that then failed or finished empty.
    /// </summary>
    public string? Diagnostic { get; set; }

    public UsageDetails? Usage { get; set; }

}

/// <summary>A conversation flattened for a one-shot CLI: instructions, and everything else.</summary>
internal sealed record FamiliarPrompt(string? SystemPrompt, string Text);

/// <summary>
/// Flattens an Arcanum conversation into what a one-shot CLI can accept.
/// </summary>
/// <remarks>
/// A Familiar is spawned per turn with no session to resume, so prior turns have to travel inside
/// the prompt. The single-user-message case — by far the most common — is passed through verbatim so
/// nothing is decorated that does not need to be; a genuine multi-turn exchange gets role labels,
/// because without them the model cannot tell its own earlier answer from the operator's question.
/// </remarks>
internal static class FamiliarPromptComposer
{

    public static FamiliarPrompt Compose(IEnumerable<ChatMessage> messages)
    {

        List<ChatMessage> conversation = [.. messages];

        string systemPrompt = string.Join(
            "\n\n",
            conversation
                .Where(static message => message.Role == ChatRole.System)
                .Select(RenderText)
                .Where(static text => text.Length > 0));

        List<ChatMessage> remaining =
            [.. conversation.Where(static message => message.Role != ChatRole.System)];

        string text = remaining is [{ } only] && only.Role == ChatRole.User
            ? RenderText(only)
            : RenderTranscript(remaining);

        return new FamiliarPrompt(
            systemPrompt.Length > 0 ? systemPrompt : null,
            text);

    }

    private static string RenderTranscript(IReadOnlyList<ChatMessage> messages)
    {

        StringBuilder transcript = new();

        foreach (ChatMessage message in messages)
        {

            string body = RenderText(message);

            if (body.Length == 0)
            {
                continue;
            }

            if (transcript.Length > 0)
            {
                _ = transcript.Append("\n\n");
            }

            _ = transcript.Append(Label(message.Role)).Append(body);

        }

        return transcript.ToString();

    }

    private static string Label(ChatRole role)
    {

        if (role == ChatRole.Assistant)
        {
            return "Assistant: ";
        }

        return role == ChatRole.Tool ? "Tool result: " : "User: ";

    }

    /// <summary>
    /// Renders every content kind Arcanum's own tool loop produces. A tool call and its result are
    /// rendered as text because a Familiar cannot participate in that loop — the exchange is history
    /// the model needs to read, not an instruction it can act on.
    /// </summary>
    private static string RenderText(ChatMessage message)
    {

        StringBuilder body = new();

        foreach (AIContent content in message.Contents)
        {

            switch (content)
            {

                case TextContent { Text.Length: > 0 } text:

                    _ = body.Append(text.Text);

                    break;

                case FunctionCallContent call:

                    _ = body.Append("[tool call: ").Append(call.Name).Append(']');

                    break;

                case FunctionResultContent result:

                    _ = body.Append(result.Result?.ToString() ?? string.Empty);

                    break;

                default:

                    break;

            }

        }

        return body.ToString().Trim();

    }

}

/// <summary>
/// Names the environment variables a Familiar must not inherit.
/// </summary>
/// <remarks>
/// A thin alias over <see cref="FamiliarSecretEnvironmentNames"/>, which lives beside the process
/// runner so the inference path and the status probe cannot drift apart on what counts as a secret.
/// </remarks>
internal static class FamiliarEnvironmentDenyList
{

    public static IReadOnlyList<string> Build(ArcanumSettings settings) =>
        FamiliarSecretEnvironmentNames.Collect(settings);

}
