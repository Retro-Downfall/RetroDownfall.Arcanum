namespace RetroDownfall.TheForge.Ux.Services.Terminal;

/// <summary>Resolved shell executable and argument prefix used before the user command.</summary>
public sealed record TerminalShellSpec(string FileName, IReadOnlyList<string> ArgumentPrefix);
