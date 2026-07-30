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
        Option<string?> temperature = new("--temperature") { Description = "Sampling temperature 0-2 (lower = more deterministic). Applies to every turn." };
        Option<string?> topP = new("--top-p") { Description = "Nucleus sampling cutoff 0-1. Applies to every turn." };
        Option<string?> maxTokens = new("--max-tokens") { Description = "Maximum output tokens per turn." };
        Option<string?> seed = new("--seed") { Description = "Seed for sampling determinism (provider support varies). Applies to every turn." };
        Option<string[]> stop = new("--stop") { AllowMultipleArgumentsPerToken = true, Description = "Stop sequence(s); pass --stop multiple times for several stops." };
        Option<string?> responseFormat = new("--response-format") { Description = "Response format: text | json_object | json_schema." };
        Option<string?> presencePenalty = new("--presence-penalty") { Description = "Presence penalty -2..2." };
        Option<string?> frequencyPenalty = new("--frequency-penalty") { Description = "Frequency penalty -2..2." };

        chat.Add(model); chat.Add(@new); chat.Add(noTools); chat.Add(unattended);
        chat.Add(campaign); chat.Add(temperature); chat.Add(topP); chat.Add(maxTokens);
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
        Option<bool> json = new("--json") { Description = "Emit the report as JSON to stdout for programmatic consumption." };

        doctor.Add(fixPermissions); doctor.Add(json);

        doctor.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Run(pr.GetValue(fixPermissions), pr.GetValue(json), ct).ConfigureAwait(false));
        return doctor;
    }

    private static Command BuildKey(IServiceProvider sp)
    {
        KeyCommands handler = sp.GetRequiredService<KeyCommands>();
        Command key = new("key", "Master API key utilities (OS credential store / security.dat fallback; no HTTP).");
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

        key.Add(show); key.Add(set);
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
        return model;
    }

    private static Command BuildProvider(IServiceProvider sp)
    {
        ProviderCommands handler = sp.GetRequiredService<ProviderCommands>();
        Command provider = new("provider", "Native provider listing and configuration summary (requires arcanum serve).");
        Command list = new("list", "List configured providers with redacted secrets (GET /api/providers).");
        list.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.List(ct).ConfigureAwait(false));
        provider.Add(list);
        return provider;
    }
}
