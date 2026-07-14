namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>A workspace path the operator can create a spell into, with a display label for pickers.</summary>
public sealed record WorkspaceOption(string Path, string Display);

/// <summary>Inputs collected by the New Spell dialog. <see cref="WorkspacePath"/> is the chosen workspace.</summary>
public sealed record NewSpellInputs(string Name, string? Description, string? Body, string WorkspacePath);

/// <summary>Inputs collected by the New Prompt dialog. <see cref="Template"/> is non-empty (a stub is supplied when blank).</summary>
public sealed record NewPromptInputs(string Name, string Version, string? Description, string Template);

/// <summary>Inputs collected by the New Session dialog. <see cref="Title"/> is optional.</summary>
public sealed record NewSessionInputs(string? Title);
