using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Master and native-provider API key utilities (local secure stores; no HTTP).
/// </summary>
[ExcludeFromCodeCoverage] // Reason: thin ISecretStore read/write wrapper; filesystem/OS-specific.
public sealed class KeyCommands(
    ISecretStore secretStore,
    IWebResearchCredentialStore webResearchCredentialStore,
    IOptions<ArcanumSettings> settings,
    IThemePalette themePalette)
{

    /// <summary>
    /// Print the stored master API key to stderr (so stdout piping does not capture the secret).
    /// </summary>
    public async Task<int> Show(CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        SecretStoreReadResult result = await secretStore.GetApiKeyReadResultAsync().ConfigureAwait(false);

        if (result.Status == SecretStoreReadStatus.Missing)
        {

            AnsiConsole.MarkupLine(
                themePalette.ErrorMarkup(
                    Markup.Escape(
                        "No master API key found. Run 'arcanum serve' once to generate and store a key, "
                        + "or 'arcanum key set' to paste one into the OS credential store.")));

            return 1;

        }

        if (result.Status == SecretStoreReadStatus.Corrupted)
        {

            AnsiConsole.MarkupLine(
                themePalette.ErrorMarkup(
                    Markup.Escape(result.Message ?? "security.dat is present but could not be decrypted.")));

            return 1;

        }

        Console.Error.WriteLine(result.Value!);

        AnsiConsole.MarkupLine(
            themePalette.MutedMarkup(
                Markup.Escape(
                    $"(Key written to stderr. Shared OS identity: {ArcanumCredentialIdentity.Service}/"
                    + $"{ArcanumCredentialIdentity.MasterApiKeyAccount}.)")));

        return 0;

    }

    /// <summary>
    /// Store a master API key in the OS credential store (and mirror to security.dat when possible).
    /// Pass the key as an argument, or omit it to read a single line from stdin.
    /// </summary>
    public async Task<int> Set(CancellationToken cancellationToken, string? apiKey = null)
    {

        cancellationToken.ThrowIfCancellationRequested();

        string? key = apiKey;

        if (string.IsNullOrWhiteSpace(key))
        {

            if (Console.IsInputRedirected)
            {

                key = (await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();

            }
            else
            {

                key = AnsiConsole.Prompt(
                    new TextPrompt<string>("Master API key:")
                        .Secret());

            }

        }

        if (string.IsNullOrWhiteSpace(key))
        {

            AnsiConsole.MarkupLine(themePalette.ErrorMarkup("API key must not be empty."));

            return 1;

        }

        await secretStore.SaveApiKeyAsync(key.Trim()).ConfigureAwait(false);

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(
                Markup.Escape(
                    $"Master API key stored ({ArcanumCredentialIdentity.Service}/"
                    + $"{ArcanumCredentialIdentity.MasterApiKeyAccount}).")));

        return 0;

    }

    /// <summary>
    /// Store a native provider credential. Provider keys are never printed after storage.
    /// </summary>
    public async Task<int> SetProvider(
        string provider,
        CancellationToken cancellationToken,
        string? apiKey = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsPerplexity(provider))
        {
            return UnknownProvider(provider);
        }

        string? key = apiKey;

        if (string.IsNullOrWhiteSpace(key))
        {
            if (Console.IsInputRedirected)
            {
                key = (await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();
            }
            else
            {
                key = AnsiConsole.Prompt(
                    new TextPrompt<string>("Perplexity API key:")
                        .Secret());
            }
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup("API key must not be empty."));

            return 1;
        }

        await webResearchCredentialStore
            .SavePerplexityApiKeyAsync(key.Trim(), cancellationToken)
            .ConfigureAwait(false);

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(
                Markup.Escape(
                    $"Perplexity API key stored ({ArcanumCredentialIdentity.Service}/"
                    + $"{ArcanumCredentialIdentity.PerplexityApiKeyAccount}).")));

        return 0;
    }

    /// <summary>
    /// Report whether a native provider credential is usable without disclosing it.
    /// </summary>
    public async Task<int> ProviderStatus(
        string provider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsPerplexity(provider))
        {
            return UnknownProvider(provider);
        }

        WebBrowsingSettings webResearch = settings.Value.ResolveWebBrowsing();
        string environmentVariable =
            EnvironmentCredentialResolver.GetWebResearchApiKeyEnvironmentVariableName(
                webResearch);
        string? environmentCredential =
            EnvironmentCredentialResolver.ResolveWebResearchApiKey(webResearch);

        if (!string.IsNullOrWhiteSpace(environmentCredential))
        {
            AnsiConsole.MarkupLine(
                themePalette.HighlightMarkup(
                    Markup.Escape(
                        $"Perplexity API key is configured through {environmentVariable}.")));

            return 0;
        }

        SecretStoreReadResult result = await webResearchCredentialStore
            .GetPerplexityApiKeyReadResultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (result.Status == SecretStoreReadStatus.Ok
            && !string.IsNullOrWhiteSpace(result.Value))
        {
            AnsiConsole.MarkupLine(
                themePalette.HighlightMarkup("Perplexity API key is configured."));

            return 0;
        }

        string message = result.Status == SecretStoreReadStatus.Corrupted
            ? result.Message ?? "The encrypted Perplexity credential could not be decrypted."
            : "No Perplexity API key is configured.";

        AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(message)));

        return 1;
    }

    /// <summary>Delete a native provider credential from all local secure stores.</summary>
    public async Task<int> DeleteProvider(
        string provider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsPerplexity(provider))
        {
            return UnknownProvider(provider);
        }

        await webResearchCredentialStore
            .DeletePerplexityApiKeyAsync(cancellationToken)
            .ConfigureAwait(false);

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup("Perplexity API key deleted."));

        return 0;
    }

    private static bool IsPerplexity(string? provider) =>
        string.Equals(provider?.Trim(), "perplexity", StringComparison.OrdinalIgnoreCase);

    private int UnknownProvider(string? provider)
    {
        AnsiConsole.MarkupLine(
            themePalette.ErrorMarkup(
                Markup.Escape(
                    $"Unknown native credential provider '{provider}'. Supported provider: perplexity.")));

        return 1;
    }

}
