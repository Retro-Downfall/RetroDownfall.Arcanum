namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// UI-neutral session-mutation gate while a turn is generating.
/// Sets footer status on <see cref="CommandCenterState"/> and returns a UI update —
/// never touches Terminal.Gui controls.
/// </summary>
internal static class CommandCenterSessionMutationGuard
{
    public const string GeneratingDenyMessage =
        "A turn is still generating. Cancel it with Ctrl+C before switching sessions.";

    /// <summary>
    /// When <paramref name="state"/>.Generating is true, sets <see cref="CommandCenterState.FooterHint"/>
    /// to <see cref="GeneratingDenyMessage"/> and returns a footer refresh update.
    /// </summary>
    /// <returns><see langword="true"/> if the mutation must be denied.</returns>
    public static bool TryDenySessionMutationWhileGenerating(
        CommandCenterState state,
        out CommandCenterUiUpdate? update)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.Generating)
        {
            update = null;
            return false;
        }

        state.FooterHint = GeneratingDenyMessage;
        update = new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshFooter);
        return true;
    }
}
