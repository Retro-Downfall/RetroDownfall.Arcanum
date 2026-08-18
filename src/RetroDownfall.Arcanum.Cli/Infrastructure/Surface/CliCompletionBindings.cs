namespace RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

/// <summary>
/// Maps a symbol to the safe dynamic-completion source that can supply its values. The tree stays
/// the authority on structure; this table only annotates which symbols have a live resource catalog
/// behind them.
///
/// Two shapes are recognised. A symbol named after its resource — <c>--campaign</c>, the
/// <c>session</c> positional — is bound by name. A positional named generically — <c>id</c>,
/// <c>name</c>, <c>identifier</c>, <c>prompt</c> — names no resource on its own and is bound from
/// the command family it sits in instead: the <c>id</c> of <c>campaign delete</c> is a Campaign,
/// while the identical spelling on <c>batch cancel</c> names no catalog Arcanum publishes and stays
/// unbound. Name-only keying cannot express that — <c>id</c> alone appears on 43 commands spanning
/// five unrelated resource kinds.
///
/// A contract test asserts every binding names a symbol that exists in the tree, pins the provider
/// each recognised shape resolves to, and holds the surface map to those resolutions. It cannot
/// assert the converse — that every resource-shaped symbol is annotated — because outside the
/// recognised shapes "resource-shaped" is visible only in the description prose. A new resource
/// family needs its row here, and nothing will fail if it is forgotten.
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

    /// <summary>
    /// Command families whose generically named positional means that family's own resource. A
    /// family absent from here leaves its positionals unbound, which is the safe direction: no
    /// suggestion beats a suggestion from the wrong catalog.
    /// </summary>
    private static readonly Dictionary<string, string> Families = new(StringComparer.Ordinal)
    {
        ["campaign"] = CliCompletionProviders.Campaign,
        ["workspace"] = CliCompletionProviders.Workspace,
        ["session"] = CliCompletionProviders.Session,
        ["spell"] = CliCompletionProviders.Spell,
        ["prompt"] = CliCompletionProviders.Prompt,
        ["apprentice"] = CliCompletionProviders.Apprentice,
        ["model"] = CliCompletionProviders.Model,
        ["provider"] = CliCompletionProviders.Provider,
    };

    /// <summary>
    /// Positional spellings that name a resource without saying which one. Every one of these also
    /// occurs on commands outside any bound family — <c>run &lt;prompt&gt;</c> is free text, and
    /// <c>batch cancel &lt;id&gt;</c> is an OpenAI-compatible identifier — so they are only ever
    /// resolved through the family.
    /// </summary>
    private static readonly string[] Generic = ["id", "identifier", "name", "prompt"];

    /// <summary>
    /// The dynamic source for <paramref name="symbolName"/> as it appears at
    /// <paramref name="commandPath"/>, or null where the symbol names nothing Arcanum can list.
    /// </summary>
    public static string? For(string commandPath, string symbolName)
    {

        ArgumentNullException.ThrowIfNull(commandPath);

        if (Bindings.TryGetValue(symbolName, out string? provider))
        {

            return provider;

        }

        return Generic.Contains(symbolName, StringComparer.Ordinal)
            ? Family(commandPath)
            : null;

    }

    public static IReadOnlyCollection<string> SymbolNames => Bindings.Keys;

    /// <summary>
    /// The resource family a command belongs to: the last path segment that names one, so both
    /// <c>campaign codex put</c> and <c>use campaign</c> resolve to Campaign.
    /// </summary>
    private static string? Family(string commandPath)
    {

        string? family = null;

        foreach (string segment in commandPath.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {

            if (Families.TryGetValue(segment, out string? provider))
            {

                family = provider;

            }

        }

        return family;

    }

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
