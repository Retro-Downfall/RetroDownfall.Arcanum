using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// A new session is where an operator who knows no keys yet starts, so its transcript opens by
/// naming the ones that find everything else: how to send, how to reach the commands, how to start
/// over or go back, and how to change the model.
/// </summary>
public sealed class CommandCenterWelcomeTests
{
    [Fact]
    public void A_new_session_opens_with_one_welcome_entry_naming_the_keys()
    {
        CommandCenterState state = new(new SessionLogBuffer());

        state.Log.Append(SessionLogEntryKind.User, "the previous conversation");

        CreateWorkspace().StartNewSession(state);

        SessionLogEntry welcome = Assert.Single(state.Log.Snapshot());

        Assert.Equal(SessionLogEntryKind.Status, welcome.Kind);

        string transcript = state.Log.RenderPlainText();

        Assert.All(CommandCenterWelcome.Lines, line => Assert.Contains(line, transcript, StringComparison.Ordinal));

        Assert.DoesNotContain("the previous conversation", transcript, StringComparison.Ordinal);
    }

    /// <summary>One entry renders as consecutive lines: the block costs its six rows and no blank separators.</summary>
    [Fact]
    public void The_welcome_renders_as_one_block_without_blank_lines()
    {
        CommandCenterState state = new(new SessionLogBuffer());

        CreateWorkspace().StartNewSession(state);

        ObservableCollection<string> rows = [];

        state.Log.CopyLinesTo(rows);

        Assert.Equal(CommandCenterWelcome.Lines, rows);
    }

    [Fact]
    public void The_welcome_names_the_keys_that_lead_to_everything_else()
    {
        Assert.Equal(
            [
                "New Session — your first message creates it.",
                "  Enter sends · Ctrl+J adds a new line",
                "  / lists commands · Ctrl+K opens the command palette",
                "  Ctrl+N new session · Ctrl+O past sessions",
                "  Shift+Tab reaches the model control (top right) to change model",
                "  F1 shows every key",
            ],
            CommandCenterWelcome.Lines);
    }

    private static SessionWorkspaceService CreateWorkspace() =>
        new(
            new ArcanumApiClient(
                new FakeHttpClientFactory(),
                ArcanumApiCredentialLeaseTestFactory.Create("test-key")),
            new NoLastSessionStore(),
            NullLogger<SessionWorkspaceService>.Instance);

    /// <summary>Starting a session reaches neither the host nor the stored last session.</summary>
    private sealed class NoLastSessionStore : ILastSessionStore
    {
        public Guid? GetLastSessionId() => null;

        public Task<ArcanumClientMutationResult<CliContextDocument>> SaveSessionIdAsync(
            Guid id,
            Func<Guid, CancellationToken, Task<Result<bool>>> revalidateAsync,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Starting a new session must not save a last session.");
    }
}
