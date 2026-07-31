using System.CommandLine;

using System.CommandLine.Parsing;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Commands.Configuration;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    private static Command BuildConfig(IServiceProvider services)
    {

        ConfigCommands handler = services.GetRequiredService<ConfigCommands>();

        Command config = new(
            "config",
            "Safely inspect, validate, edit, and open Arcanum configuration.");

        Command path = new("path", "Print the exact arcanum.json path.");

        path.SetAction((ParseResult _) => handler.Path());

        Command show = new("show", "Show the effective configuration with secrets redacted.");

        show.SetAction(
            async (ParseResult _, CancellationToken cancellationToken) =>
                await handler.Show(cancellationToken).ConfigureAwait(false));

        Command get = new("get", "Show one descriptor-backed configuration value.");

        Argument<string> getKey = new("key")
        {

            Description = "Dot path such as host.port or providers.0.endpoint.",

        };

        get.Add(getKey);

        get.SetAction(
            async (ParseResult parseResult, CancellationToken cancellationToken) =>
                await handler.Get(
                        parseResult.GetValue(getKey)!,
                        cancellationToken)
                    .ConfigureAwait(false));

        Command set = new(
            "set",
            "Parse, validate, and atomically set one descriptor-backed value.");

        Argument<string> setKey = new("key")
        {

            Description = "Dot path such as host.port or providers.0.endpoint.",

        };

        Argument<string?> setValue = new("value")
        {

            Arity = ArgumentArity.ZeroOrOne,

            Description = "Typed value; omit for sensitive fields and use secure input.",

        };

        set.Add(setKey);

        set.Add(setValue);

        set.SetAction(
            async (ParseResult parseResult, CancellationToken cancellationToken) =>
                await handler.Set(
                        parseResult.GetValue(setKey)!,
                        parseResult.GetValue(setValue),
                        cancellationToken)
                    .ConfigureAwait(false));

        Command validate = new(
            "validate",
            "Validate the complete effective configuration without writing it.");

        validate.SetAction(
            async (ParseResult _, CancellationToken cancellationToken) =>
                await handler.Validate(cancellationToken).ConfigureAwait(false));

        Command edit = new(
            "edit",
            "Edit an owner-only temporary copy, validate it, and atomically apply it.");

        edit.SetAction(
            async (ParseResult _, CancellationToken cancellationToken) =>
                await handler.Edit(cancellationToken).ConfigureAwait(false));

        Command open = new("open", "Launch Compendium for visual configuration editing.");

        open.SetAction((ParseResult _) => handler.Open());

        config.Add(path);

        config.Add(show);

        config.Add(get);

        config.Add(set);

        config.Add(validate);

        config.Add(edit);

        config.Add(open);

        return config;

    }

}
