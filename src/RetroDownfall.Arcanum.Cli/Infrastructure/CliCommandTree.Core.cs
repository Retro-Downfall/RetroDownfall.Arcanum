using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Commands.Configuration;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{
    private static Command BuildChat(IServiceProvider sp)
    {
        ChatCommand handler = sp.GetRequiredService<ChatCommand>();
        Command chat = new("chat", "Interactive multi-turn REPL with the Mage.");
        Option<string?> model = new("--model", "-m") { Description = "The specific model to use for this inference request." };
        Option<bool> @new = new("--new", "-n") { Description = "Start a new session thread, clearing the previous session at REPL startup." };
        Option<bool> noTools = new("--no-tools") { Description = "Disable MCP-provided tools for this REPL session (built-in tools still apply)." };
        Option<bool> unattended = new("--unattended") { Description = "Force unattended for this run; skips ask_human blocking and uses Ward auto-deny." };
        Option<string?> campaign = new("--campaign", "-c") { Description = "Campaign GUID to resolve the workspace from." };
        Option<string?> workspace = new("--workspace") { Description = "Workspace ID or path for this chat." };
        Option<string?> session = new("--session") { Description = "Session GUID, exact title, or unique title prefix to resume." };
        Option<string?> temperature = new("--temperature") { Description = "Sampling temperature 0-2 (lower = more deterministic). Applies to every turn." };
        Option<string?> topP = new("--top-p") { Description = "Nucleus sampling cutoff 0-1. Applies to every turn." };
        Option<string?> maxTokens = new("--max-tokens") { Description = "Maximum output tokens per turn." };
        Option<string?> seed = new("--seed") { Description = "Seed for sampling determinism (provider support varies). Applies to every turn." };
        Option<string[]> stop = new("--stop") { AllowMultipleArgumentsPerToken = true, Description = "Stop sequence(s); pass --stop multiple times for several stops." };
        Option<string?> responseFormat = new("--response-format") { Description = "Response format: text | json_object | json_schema." };
        Option<string?> presencePenalty = new("--presence-penalty") { Description = "Presence penalty -2..2." };
        Option<string?> frequencyPenalty = new("--frequency-penalty") { Description = "Frequency penalty -2..2." };

        chat.Add(model); chat.Add(@new); chat.Add(noTools); chat.Add(unattended);
        chat.Add(campaign); chat.Add(workspace); chat.Add(session);
        chat.Add(temperature); chat.Add(topP); chat.Add(maxTokens);
        chat.Add(seed); chat.Add(stop); chat.Add(responseFormat); chat.Add(presencePenalty);
        chat.Add(frequencyPenalty);

        chat.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            return await handler.Chat(
                ct,
                pr.GetValue(model),
                pr.GetValue(@new),
                pr.GetValue(noTools),
                pr.GetValue(unattended),
                pr.GetValue(campaign),
                pr.GetValue(workspace),
                pr.GetValue(session),
                pr.GetValue(temperature),
                pr.GetValue(topP),
                pr.GetValue(maxTokens),
                pr.GetValue(seed),
                pr.GetValue(stop) ?? [],
                pr.GetValue(responseFormat),
                pr.GetValue(presencePenalty),
                pr.GetValue(frequencyPenalty)).ConfigureAwait(false);
        });
        return chat;
    }

    private static Command BuildDoctor(IServiceProvider sp)
    {
        DoctorCommand handler = sp.GetRequiredService<DoctorCommand>();
        Command doctor = new("doctor", "Run environment diagnostics (version, paths, API health).");
        Option<bool> fixPermissions = new("--fix-permissions") { Description = "Apply owner-only permissions to the Grimoire database, arcanum.json, and secret store." };

        doctor.Add(fixPermissions);

        doctor.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Run(
                pr.GetValue(fixPermissions),
                CliInvocationContext.Current.Json,
                ct).ConfigureAwait(false));
        return doctor;
    }

    private static Command BuildKey(IServiceProvider sp)
    {
        KeyCommands handler = sp.GetRequiredService<KeyCommands>();
        Command key = new("key", "Master and native-provider API key utilities (secure local stores; no HTTP).");
        Command show = new("show", "Print the stored master API key to stderr (stdout piping does not capture the secret).");
        Command set = new("set", "Store a master API key in the OS credential store (mirrors to security.dat when possible).");
        Argument<string?> apiKey = new("api-key")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "The master API key to store; omit to read a single line from stdin or a secure prompt.",
        };

        set.Add(apiKey);

        show.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Show(ct).ConfigureAwait(false));
        set.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Set(ct, pr.GetValue(apiKey)).ConfigureAwait(false));

        Command provider = new(
            "provider",
            "Manage native provider credentials. Stored values are never displayed.");
        Command providerSet = new(
            "set",
            "Store a native provider credential from redirected stdin or a secure prompt.");
        Command providerStatus = new("status", "Report whether a native provider credential is configured.");
        Command providerDelete = new("delete", "Delete a native provider credential from local secure stores.");
        Argument<string> providerForSet = new("provider")
        {
            Description = "Native provider name (currently: perplexity).",
        };
        Argument<string> providerForStatus = new("provider")
        {
            Description = "Native provider name (currently: perplexity).",
        };
        Argument<string> providerForDelete = new("provider")
        {
            Description = "Native provider name (currently: perplexity).",
        };
        providerSet.Add(providerForSet);
        providerStatus.Add(providerForStatus);
        providerDelete.Add(providerForDelete);

        providerSet.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.SetProvider(
                pr.GetValue(providerForSet)!,
                ct).ConfigureAwait(false));
        providerStatus.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.ProviderStatus(pr.GetValue(providerForStatus)!, ct).ConfigureAwait(false));
        providerDelete.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.DeleteProvider(pr.GetValue(providerForDelete)!, ct).ConfigureAwait(false));

        provider.Add(providerSet);
        provider.Add(providerStatus);
        provider.Add(providerDelete);

        key.Add(show); key.Add(set); key.Add(provider);
        return key;
    }

    private static Command BuildLook(IServiceProvider sp)
    {
        LookCommand handler = sp.GetRequiredService<LookCommand>();
        Command look = new("look", "Eye of the World: situational snapshot of the current directory (domain + TOC).");
        look.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Run(ct).ConfigureAwait(false));
        return look;
    }

    private static Command BuildModel(IServiceProvider sp)
    {
        ModelCommands handler = sp.GetRequiredService<ModelCommands>();
        Command model = new("model", "Native model listing across configured providers (requires arcanum serve).");
        Command list = new("list", "List configured models across all providers (GET /api/models).");
        list.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.List(ct).ConfigureAwait(false));
        model.Add(list);
        Command get = new("get", "Show a configured model without exposing its endpoint.");
        Argument<string?> identifier = OptionalResourceArgument("model", "model name or provider/model ID");
        get.Add(identifier);
        get.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Get(pr.GetValue(identifier), ct).ConfigureAwait(false));
        model.Add(get);
        return model;
    }

    private static Command BuildProvider(IServiceProvider sp)
    {
        ProviderCommands handler = sp.GetRequiredService<ProviderCommands>();
        Command provider = new("provider", "Native provider listing and configuration summary (requires arcanum serve).");
        Command list = new("list", "List configured providers with redacted secrets (GET /api/providers).");
        list.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.List(ct).ConfigureAwait(false));
        provider.Add(list);
        Command get = new("get", "Show a configured provider without exposing endpoint or credential details.");
        Argument<string?> identifier = OptionalResourceArgument("provider", "provider name");
        get.Add(identifier);
        get.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Get(pr.GetValue(identifier), ct).ConfigureAwait(false));
        provider.Add(get);
        return provider;
    }

    private static Command BuildWorkspace(IServiceProvider sp)
    {

        WorkspaceCommands handler = sp.GetRequiredService<WorkspaceCommands>();

        Command workspace = new(
            "workspace",
            "Workspace = registered filesystem access/indexing boundary. Campaign = persistent project container with sessions, spells, prompts, Codex, and Sanctum. Paths are resolved on the server host.");

        Command list = new("list", "List registered workspaces.");

        list.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.List(ct).ConfigureAwait(false));

        Command current = new(
            "current",
            "Map the client current directory to registered server Workspace and Campaign resources.");

        current.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Current(ct).ConfigureAwait(false));

        Command register = new(
            "register",
            "Register a server-host path; omit path to register this directory when client and server share a host.");

        Argument<string?> registerPath = new("path")
        {

            Arity = ArgumentArity.ZeroOrOne,

            Description = "Server-host filesystem path; defaults to the client current directory for the bundled local server.",

        };

        Option<string?> registerName = new("--name")
        {

            Description = "Workspace display name; defaults to the path's final segment.",

        };

        Option<string?> registerType = new("--type")
        {

            Description = "Workspace type: spell, campaign, data, or custom (default).",

        };

        register.Add(registerPath);

        register.Add(registerName);

        register.Add(registerType);

        register.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Register(
                    pr.GetValue(registerPath),
                    pr.GetValue(registerName),
                    pr.GetValue(registerType),
                    ct).ConfigureAwait(false));

        Command show = new("show", "Show one registered workspace and its server-host path.");

        Argument<string?> showIdentifier = OptionalResourceArgument(
            "workspace",
            "workspace ID, name, or server path");

        show.Add(showIdentifier);

        show.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Show(pr.GetValue(showIdentifier), ct).ConfigureAwait(false));

        Command get = new("get", "Compatibility alias for 'workspace show'.");

        Argument<string?> getIdentifier = OptionalResourceArgument(
            "workspace",
            "workspace ID, name, or server path");

        get.Add(getIdentifier);

        get.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Show(pr.GetValue(getIdentifier), ct).ConfigureAwait(false));

        Command tree = new("tree", "List the server-side workspace tree recursively.");

        Argument<string?> treeWorkspace = OptionalResourceArgument(
            "workspace",
            "workspace ID, name, or server path");

        Option<string?> treePath = new("--path")
        {

            Description = "Optional relative path inside the selected workspace.",

        };

        tree.Add(treeWorkspace);

        tree.Add(treePath);

        tree.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Tree(
                pr.GetValue(treeWorkspace),
                pr.GetValue(treePath),
                ct).ConfigureAwait(false));

        Command info = new("info", "Inspect a path through the server workspace API.");

        Argument<string> infoPath = new("path")
        {

            Description = "Relative path within the selected server workspace.",

        };

        Option<string?> infoWorkspace = WorkspaceOption();

        info.Add(infoPath);

        info.Add(infoWorkspace);

        info.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Info(
                pr.GetValue(infoPath)!,
                pr.GetValue(infoWorkspace),
                ct).ConfigureAwait(false));

        Command read = new("read", "Read a file through the bounded server workspace API.");

        Argument<string> readPath = new("path")
        {

            Description = "Relative file path within the selected server workspace.",

        };

        Option<string?> readWorkspace = WorkspaceOption();

        read.Add(readPath);

        read.Add(readWorkspace);

        read.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Read(
                pr.GetValue(readPath)!,
                pr.GetValue(readWorkspace),
                ct).ConfigureAwait(false));

        Command search = new("search", "Semantically search the selected workspace's server-side index.");

        Argument<string> searchQuery = new("query")
        {

            Description = "Semantic search query.",

        };

        Option<string?> searchWorkspace = WorkspaceOption();

        Option<int?> searchLimit = new("--limit")
        {

            Description = "Optional bounded result count.",

        };

        search.Add(searchQuery);

        search.Add(searchWorkspace);

        search.Add(searchLimit);

        search.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Search(
                pr.GetValue(searchQuery)!,
                pr.GetValue(searchWorkspace),
                pr.GetValue(searchLimit),
                ct).ConfigureAwait(false));

        Command index = new("index", "Request a server-side workspace re-index.");

        Argument<string?> indexWorkspace = OptionalResourceArgument(
            "workspace",
            "workspace ID, name, or server path");

        index.Add(indexWorkspace);

        index.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Index(pr.GetValue(indexWorkspace), ct).ConfigureAwait(false));

        Command indexStatus = new("index-status", "Show server-side workspace indexing status.");

        Argument<string?> statusWorkspace = OptionalResourceArgument(
            "workspace",
            "workspace ID, name, or server path");

        indexStatus.Add(statusWorkspace);

        indexStatus.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.IndexStatus(pr.GetValue(statusWorkspace), ct).ConfigureAwait(false));

        Command chunks = new("chunks", "Inspect bounded previews of server-side indexed chunks.");

        Argument<string?> chunksWorkspace = OptionalResourceArgument(
            "workspace",
            "workspace ID, name, or server path");

        Option<string?> chunksPath = new("--path")
        {

            Description = "Optional relative-path filter.",

        };

        Option<int?> chunksLimit = new("--limit");

        Option<int?> chunksOffset = new("--offset");

        chunks.Add(chunksWorkspace);

        chunks.Add(chunksPath);

        chunks.Add(chunksLimit);

        chunks.Add(chunksOffset);

        chunks.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Chunks(
                pr.GetValue(chunksWorkspace),
                pr.GetValue(chunksPath),
                pr.GetValue(chunksLimit),
                pr.GetValue(chunksOffset),
                ct).ConfigureAwait(false));

        Command unregister = new("unregister", "Remove a workspace registration without deleting files.");

        Argument<string?> unregisterWorkspace = OptionalResourceArgument(
            "workspace",
            "workspace ID, name, or server path");

        unregister.Add(unregisterWorkspace);

        unregister.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Unregister(
                pr.GetValue(unregisterWorkspace),
                ct).ConfigureAwait(false));

        workspace.Add(list);

        workspace.Add(current);

        workspace.Add(register);

        workspace.Add(show);

        workspace.Add(get);

        workspace.Add(tree);

        workspace.Add(info);

        workspace.Add(read);

        workspace.Add(search);

        workspace.Add(index);

        workspace.Add(indexStatus);

        workspace.Add(chunks);

        workspace.Add(unregister);

        return workspace;

    }

    private static Option<string?> WorkspaceOption() =>
        new("--workspace")
        {

            Description = "Workspace ID, name, or server path; defaults to saved context or current-path detection.",

        };

    private static Command BuildMcp(IServiceProvider sp)
    {
        McpCommands handler = sp.GetRequiredService<McpCommands>();
        Command mcp = new("mcp", "MCP server status without secret-adjacent configuration details.");
        Command list = new("list", "List MCP server status.");
        list.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.List(ct).ConfigureAwait(false));
        Command get = new("get", "Show one MCP server's safe status summary.");
        Argument<string?> identifier = OptionalResourceArgument("server", "MCP server name");
        get.Add(identifier);
        get.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Get(pr.GetValue(identifier), ct).ConfigureAwait(false));
        mcp.Add(list);
        mcp.Add(get);
        return mcp;
    }

    private static Argument<string?> OptionalResourceArgument(string name, string accepted)
    {
        return new Argument<string?>(name)
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = $"Optional {accepted}; omit for an interactive picker.",
        };
    }
}
