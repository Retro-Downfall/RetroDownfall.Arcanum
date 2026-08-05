using System.CommandLine;

using System.CommandLine.Parsing;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Commands;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    private static Command BuildSetup(IServiceProvider services)
    {

        SetupCommand handler = services.GetRequiredService<SetupCommand>();

        Command setup = new(
            "setup",
            "Guided, resumable first-run setup: provider, credentials, workspace, and preset.");

        Option<bool> plan = new("--plan")
        {

            Description = "Compute and print the plan without writing anything.",

        };

        Option<bool> apply = new("--apply")
        {

            Description = "Apply the plan without prompting; requires every needed value up front.",

        };

        Option<string?> preset = new("--preset")
        {

            Description = "Onboarding preset ID to apply (see 'arcanum preset list').",

        };

        Option<string?> providerName = new("--provider")
        {

            Description = "Provider name to create or update.",

        };

        Option<string?> providerEndpoint = new("--endpoint")
        {

            Description = "OpenAI-compatible provider endpoint, including the /v1 suffix.",

        };

        Option<string?> model = new("--model")
        {

            Description = "Default model advertised by the provider.",

        };

        Option<string?> providerKeyEnvironment = new("--provider-key-env")
        {

            Description =
                "Environment variable holding the provider API key. No secret is read or stored.",

        };

        Option<bool> providerKeyStdin = new("--provider-key-stdin")
        {

            Description =
                "Read the provider API key as the first line of redirected stdin and store it "
                + "securely. Secrets are never accepted in arguments.",

        };

        Option<bool> clearProviderKey = new("--no-provider-key")
        {

            Description = "Delete any stored provider credential (for keyless local servers).",

        };

        Option<bool?> research = new("--research")
        {

            Description = "Enable (true) or skip (false) the Perplexity web-research credential step.",

        };

        Option<string?> researchKeyEnvironment = new("--research-key-env")
        {

            Description =
                "Environment variable holding the web-research API key. No secret is read or stored.",

        };

        Option<bool> researchKeyStdin = new("--research-key-stdin")
        {

            Description =
                "Read the web-research API key from redirected stdin (after the provider key when "
                + "both are supplied) and store it securely.",

        };

        Option<string?> workspace = new("--workspace")
        {

            Description = "Default workspace root to register in configuration.",

        };

        Option<string?> campaign = new("--campaign")
        {

            Description = "Campaign name recorded in the completion summary and CLI context.",

        };

        Option<string?> edition = new("--edition")
        {

            Description = "Runtime edition: local or development.",

        };

        Option<bool?> listenAny = new("--listen-any")
        {

            Description =
                "Privacy posture: bind all network interfaces (requires HTTPS) instead of loopback.",

        };

        Option<bool> allowUnreachable = new("--allow-unreachable-provider")
        {

            Description =
                "Commit even when live provider validation fails (air-gapped or not-yet-started hosts).",

        };

        setup.Add(plan);
        setup.Add(apply);
        setup.Add(preset);
        setup.Add(providerName);
        setup.Add(providerEndpoint);
        setup.Add(model);
        setup.Add(providerKeyEnvironment);
        setup.Add(providerKeyStdin);
        setup.Add(clearProviderKey);
        setup.Add(research);
        setup.Add(researchKeyEnvironment);
        setup.Add(researchKeyStdin);
        setup.Add(workspace);
        setup.Add(campaign);
        setup.Add(edition);
        setup.Add(listenAny);
        setup.Add(allowUnreachable);

        setup.SetAction(
            async (ParseResult parseResult, CancellationToken cancellationToken) =>
                await handler.RunAsync(
                        new SetupCommandOptions(
                            parseResult.GetValue(plan),
                            parseResult.GetValue(apply),
                            parseResult.GetValue(preset),
                            parseResult.GetValue(providerName),
                            parseResult.GetValue(providerEndpoint),
                            parseResult.GetValue(model),
                            parseResult.GetValue(providerKeyEnvironment),
                            parseResult.GetValue(providerKeyStdin),
                            parseResult.GetValue(clearProviderKey),
                            parseResult.GetValue(research),
                            parseResult.GetValue(researchKeyEnvironment),
                            parseResult.GetValue(researchKeyStdin),
                            parseResult.GetValue(workspace),
                            parseResult.GetValue(campaign),
                            parseResult.GetValue(edition),
                            parseResult.GetValue(listenAny),
                            parseResult.GetValue(allowUnreachable)),
                        cancellationToken)
                    .ConfigureAwait(false));

        return setup;

    }

}
