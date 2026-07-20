using RetroDownfall.Arcanum.Cli.CommandCenter;
using Xunit;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterComposerPreservationTests
{
    [Fact]
    public void TryBeginTurn_WhenAlreadyActive_ReturnsFalse_PreservingAdmissionGate()
    {
        CommandCenterState state = new(new SessionLogBuffer());
        Assert.True(state.TryBeginTurn());
        Assert.False(state.TryBeginTurn());

        // Staged attachments remain until a successful Result path clears them.
        _ = state.StagedAttachmentPaths.Add("/tmp/note.txt");
        Guid stagedId = Guid.NewGuid();
        _ = state.StagedAttachmentReferences.Add(stagedId);

        Assert.False(state.TryBeginTurn());
        Assert.Contains("/tmp/note.txt", state.StagedAttachmentPaths);
        Assert.Contains(stagedId, state.StagedAttachmentReferences);

        state.EndTurn();
        Assert.True(state.TryBeginTurn());
    }
}
