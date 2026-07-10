namespace RetroDownfall.TheForge.Ux.Services.Terminal;

/// <summary>One line of stdout or stderr from a running Hearth command.</summary>
public sealed record TerminalOutputEvent(string Text, TerminalOutputKind Kind);
