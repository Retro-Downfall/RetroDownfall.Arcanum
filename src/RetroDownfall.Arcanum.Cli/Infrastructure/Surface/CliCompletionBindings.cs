namespace RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

/// <summary>
/// Maps a symbol name to the safe dynamic-completion source that can supply its values. The tree
/// stays the authority on structure; this table only annotates which symbols have a live resource
/// catalog behind them. A contract test asserts both directions — every binding names a symbol that
/// exists, and every resource-shaped symbol has a binding — so the annotation cannot drift.
///
/// Only bounded, non-secret catalog projections appear here. Prompt text, transcripts, endpoints,
/// credentials, MCP commands/arguments/environment, attachment contents, and tool arguments are
/// deliberately absent and must never be added.
/// </summary>
internal static class CliCompletionBindings
{

    private static readonly Dictionary<string, string> Bindings = new(StringComparer.Ordinal)
    {
        ["--model"] = CliCompletionProviders.Model,
        ["--provider"] = CliCompletionProviders.Provider,
        ["--campaign"] = CliCompletionProviders.Campaign,
        ["--workspace"] = CliCompletionProviders.Workspace,
        ["--session"] = CliCompletionProviders.Session,
        ["--spell"] = CliCompletionProviders.Spell,
        ["--server"] = CliCompletionProviders.McpServer,
        ["campaign"] = CliCompletionProviders.Campaign,
        ["workspace"] = CliCompletionProviders.Workspace,
        ["session"] = CliCompletionProviders.Session,
        ["spell"] = CliCompletionProviders.Spell,
        ["prompt-name"] = CliCompletionProviders.Prompt,
        ["apprentice"] = CliCompletionProviders.Apprentice,
        ["model"] = CliCompletionProviders.Model,
        ["provider"] = CliCompletionProviders.Provider,
        ["server"] = CliCompletionProviders.McpServer,
    };

    public static string? For(string symbolName) =>
        Bindings.TryGetValue(symbolName, out string? provider)
            ? provider
            : null;

    public static IReadOnlyCollection<string> SymbolNames => Bindings.Keys;

}

/// <summary>
/// Stable provider identifiers embedded in generated completion scripts. Renaming one changes
/// every generated script, so they are treated as a published contract.
/// </summary>
internal static class CliCompletionProviders
{

    public const string Model = "model";

    public const string Provider = "provider";

    public const string Campaign = "campaign";

    public const string Workspace = "workspace";

    public const string Session = "session";

    public const string Spell = "spell";

    public const string Prompt = "prompt";

    public const string Apprentice = "apprentice";

    public const string McpServer = "mcp-server";

    public static readonly string[] All =
    [
        Model,
        Provider,
        Campaign,
        Workspace,
        Session,
        Spell,
        Prompt,
        Apprentice,
        McpServer,
    ];

}
