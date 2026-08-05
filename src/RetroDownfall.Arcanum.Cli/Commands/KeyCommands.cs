using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Secrets.Security;

using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Master, inference-provider, and web-research credential utilities (local secure stores; no HTTP).
/// Every read-only surface reports presence/status and fixed recovery guidance only — a stored
/// credential is never echoed back, including in <c>--json</c> and debug output (§8.1).
/// </summary>
public sealed class KeyCommands(
    ISecretStore secretStore,
    IWebResearchCredentialStore webResearchCredentialStore,
    IProviderCredentialStore providerCredentialStore,
    IOptions<ArcanumSettings> settings,
    IConsoleDispatcher console,
    ICliInvocationContext invocationContext)
{

    /// <summary>Reserved credential name routed to the native web-research provider by default.</summary>
    public const string WebResearchProviderName = "perplexity";

    private const string InferenceKind = "inference";

    private const string WebResearchKind = "web-research";

    private const string ProviderRecovery =
        "Store or replace it with 'arcanum setup' or "
        + "'arcanum key provider set <provider>'; it is never printed back.";

    private const string MasterRecovery =
        "Run 'arcanum serve' once to generate it, or 'arcanum key set' to store an existing key.";

    private const string GrimoireRecovery =
        "Generated on first Grimoire use. A corrupt secret is never replaced automatically while "
        + "encrypted data exists; see docs/Arcanum.DEBUGGING.Human.md.";

    private const string FileEncryptionRecovery =
        "Recover from the OS credential store, the Data Protection mirror plus key ring, or one "
        + "verified .arcbackup generation; a replacement key is never generated while ciphertext exists.";

    private const string WebResearchRecovery =
        "Store or replace it with 'arcanum setup' or 'arcanum key provider set perplexity'.";

    /// <summary>
    /// Print the stored master API key to stderr (so stdout piping does not capture the secret).
    /// </summary>
    public async Task<int> Show(CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        SecretStoreReadResult result = await secretStore
            .GetApiKeyReadResultAsync()
            .ConfigureAwait(false);

        if (result.Status == SecretStoreReadStatus.Missing)
        {

            console.WriteDiagnostic(
                "No master API key found. Run 'arcanum serve' once to generate and store a key, "
                + "or 'arcanum key set' to paste one into the OS credential store.");

            return (int)CliExitCode.ConfigurationError;

        }

        if (result.Status == SecretStoreReadStatus.Corrupted)
        {

            console.WriteDiagnostic(
                result.Message ?? "security.dat is present but could not be decrypted.");

            return (int)CliExitCode.ConfigurationError;

        }

        Console.Error.WriteLine(result.Value!);

        console.WriteDiagnostic(
            $"(Key written to stderr. Shared OS identity: {ArcanumCredentialIdentity.Service}/"
            + $"{ArcanumCredentialIdentity.MasterApiKeyAccount}.)");

        return (int)CliExitCode.Success;

    }

    /// <summary>
    /// Store a master API key in the OS credential store (and mirror to security.dat when possible).
    /// Pass the key as an argument, or omit it to read a single line from stdin.
    /// </summary>
    public async Task<int> Set(CancellationToken cancellationToken, string? apiKey = null)
    {

        cancellationToken.ThrowIfCancellationRequested();

        string? key = await ReadCredentialAsync(apiKey, "Master API key:", cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(key))
        {

            console.WriteDiagnostic("API key must not be empty.");

            return (int)CliExitCode.ConfigurationError;

        }

        await secretStore.SaveApiKeyAsync(key.Trim()).ConfigureAwait(false);

        console.WritePayload(
            $"Master API key stored ({ArcanumCredentialIdentity.Service}/"
            + $"{ArcanumCredentialIdentity.MasterApiKeyAccount}).");

        return (int)CliExitCode.Success;

    }

    /// <summary>
    /// Report every Arcanum-owned credential identity with presence/status, storage class, resolved
    /// source, environment reference, and fixed recovery guidance. Values are never included.
    /// </summary>
    public async Task<int> Inventory(CancellationToken cancellationToken)
    {

        List<CredentialInventoryEntryPayload> entries =
        [
            await BuildMasterEntryAsync(cancellationToken).ConfigureAwait(false),
            await BuildGrimoireEntryAsync(cancellationToken).ConfigureAwait(false),
            await BuildFileEncryptionEntryAsync(cancellationToken).ConfigureAwait(false),
            await BuildWebResearchEntryAsync(cancellationToken).ConfigureAwait(false),
        ];

        foreach (ProviderSettings provider in settings.Value.Providers ?? [])
        {

            if (string.IsNullOrWhiteSpace(provider.Name))
            {

                continue;

            }

            entries.Add(
                await BuildInferenceProviderEntryAsync(provider, cancellationToken)
                    .ConfigureAwait(false));

        }

        CredentialInventoryPayload payload = new([.. entries]);

        if (invocationContext.Options.Json)
        {

            console.WriteJson(payload, CliJsonContext.Default.CredentialInventoryPayload);

            return (int)CliExitCode.Success;

        }

        console.WritePayload("Arcanum credential inventory (presence and status only)");

        foreach (CredentialInventoryEntryPayload entry in payload.Credentials)
        {

            console.WritePayload($"{entry.DisplayName} [{entry.Kind}] — {entry.Status}");

            console.WritePayload($"  Storage: {entry.Storage}");

            console.WritePayload($"  Resolved source: {entry.Source}");

            console.WritePayload(
                "  Environment reference: " + (entry.EnvironmentVariable ?? "none"));

            if (entry.Status != "configured")
            {

                console.WritePayload($"  Recovery: {entry.Recovery}");

            }

        }

        return (int)CliExitCode.Success;

    }

    /// <summary>
    /// Store an inference-provider or web-research credential. Stored values are never printed.
    /// </summary>
    public async Task<int> SetProvider(
        string provider,
        CancellationToken cancellationToken,
        string? kind = null,
        string? apiKey = null)
    {

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryResolveKind(provider, kind, out bool webResearch))
        {

            return (int)CliExitCode.ConfigurationError;

        }

        string prompt = webResearch
            ? "Perplexity API key:"
            : $"API key for provider '{provider.Trim()}':";

        string? key = await ReadCredentialAsync(apiKey, prompt, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(key))
        {

            console.WriteDiagnostic("API key must not be empty.");

            return (int)CliExitCode.ConfigurationError;

        }

        if (webResearch)
        {

            await webResearchCredentialStore
                .SavePerplexityApiKeyAsync(key.Trim(), cancellationToken)
                .ConfigureAwait(false);

            console.WritePayload(
                $"Perplexity API key stored ({ArcanumCredentialIdentity.Service}/"
                + $"{ArcanumCredentialIdentity.PerplexityApiKeyAccount}).");

            return (int)CliExitCode.Success;

        }

        await providerCredentialStore
            .SaveApiKeyAsync(provider.Trim(), key.Trim(), cancellationToken)
            .ConfigureAwait(false);

        console.WritePayload(
            $"Provider API key stored ({ArcanumCredentialIdentity.Service}/"
            + $"{ArcanumCredentialIdentity.InferenceProviderApiKeyAccount(provider.Trim())}).");

        return (int)CliExitCode.Success;

    }

    /// <summary>
    /// Report whether a provider credential is usable without disclosing it.
    /// </summary>
    public async Task<int> ProviderStatus(
        string provider,
        CancellationToken cancellationToken,
        string? kind = null)
    {

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryResolveKind(provider, kind, out bool webResearch))
        {

            return (int)CliExitCode.ConfigurationError;

        }

        CredentialInventoryEntryPayload entry = webResearch
            ? await BuildWebResearchEntryAsync(cancellationToken).ConfigureAwait(false)
            : await BuildInferenceProviderEntryAsync(
                    FindProvider(provider) ?? new ProviderSettings { Name = provider.Trim() },
                    cancellationToken)
                .ConfigureAwait(false);

        if (invocationContext.Options.Json)
        {

            console.WriteJson(entry, CliJsonContext.Default.CredentialInventoryEntryPayload);

        }
        else
        {

            console.WritePayload($"{entry.DisplayName} [{entry.Kind}] — {entry.Status}");

            console.WritePayload($"  Resolved source: {entry.Source}");

            console.WritePayload(
                "  Environment reference: " + (entry.EnvironmentVariable ?? "none"));

            if (entry.Status != "configured")
            {

                console.WritePayload($"  Recovery: {entry.Recovery}");

            }

        }

        return entry.Status == "configured"

            ? (int)CliExitCode.Success
            : (int)CliExitCode.ConfigurationError;

    }

    /// <summary>Delete one provider credential from all local secure stores.</summary>
    public async Task<int> DeleteProvider(
        string provider,
        CancellationToken cancellationToken,
        string? kind = null)
    {

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryResolveKind(provider, kind, out bool webResearch))
        {

            return (int)CliExitCode.ConfigurationError;

        }

        if (webResearch)
        {

            await webResearchCredentialStore
                .DeletePerplexityApiKeyAsync(cancellationToken)
                .ConfigureAwait(false);

            console.WritePayload("Perplexity API key deleted.");

            return (int)CliExitCode.Success;

        }

        await providerCredentialStore
            .DeleteApiKeyAsync(provider.Trim(), cancellationToken)
            .ConfigureAwait(false);

        console.WritePayload($"Provider API key deleted for '{provider.Trim()}'.");

        return (int)CliExitCode.Success;

    }

    private async Task<string?> ReadCredentialAsync(
        string? provided,
        string prompt,
        CancellationToken cancellationToken)
    {

        if (!string.IsNullOrWhiteSpace(provided))
        {

            return provided;

        }

        if (Console.IsInputRedirected)
        {

            return (await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();

        }

        return AnsiConsole.Prompt(new TextPrompt<string>(prompt).Secret());

    }

    private bool TryResolveKind(string? provider, string? kind, out bool webResearch)
    {

        webResearch = false;

        if (string.IsNullOrWhiteSpace(provider))
        {

            console.WriteDiagnostic(
                "A provider name is required. Use 'arcanum key list' to see every credential identity.");

            return false;

        }

        if (string.IsNullOrWhiteSpace(kind))
        {

            webResearch = string.Equals(
                provider.Trim(),
                WebResearchProviderName,
                StringComparison.OrdinalIgnoreCase);

            return true;

        }

        string normalized = kind.Trim();

        if (string.Equals(normalized, WebResearchKind, StringComparison.OrdinalIgnoreCase))
        {

            webResearch = true;

            return true;

        }

        if (string.Equals(normalized, InferenceKind, StringComparison.OrdinalIgnoreCase))
        {

            return true;

        }

        console.WriteDiagnostic(
            $"Unknown credential kind '{kind}'. Supported kinds: {InferenceKind}, {WebResearchKind}.");

        return false;

    }

    private ProviderSettings? FindProvider(string name) =>
        (settings.Value.Providers ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    private async Task<CredentialInventoryEntryPayload> BuildMasterEntryAsync(
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        SecretStoreReadResult result = await secretStore
            .GetApiKeyReadResultAsync()
            .ConfigureAwait(false);

        return new CredentialInventoryEntryPayload(
            ArcanumCredentialIdentity.MasterApiKeyAccount,
            "master",
            "Master API key",
            "os-credential-store-with-encrypted-mirror",
            Status(result),
            result.Status == SecretStoreReadStatus.Ok ? "secure-store" : "none",
            null,
            MasterRecovery);

    }

    private async Task<CredentialInventoryEntryPayload> BuildGrimoireEntryAsync(
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        SecretStoreReadResult result = await secretStore
            .GetGrimoireEncryptionSecretReadResultAsync()
            .ConfigureAwait(false);

        return new CredentialInventoryEntryPayload(
            "grimoire-encryption-secret",
            "grimoire-encryption",
            "Grimoire encryption secret",
            "os-credential-store-with-encrypted-mirror",
            Status(result),
            result.Status == SecretStoreReadStatus.Ok ? "secure-store" : "none",
            null,
            GrimoireRecovery);

    }

    private async Task<CredentialInventoryEntryPayload> BuildFileEncryptionEntryAsync(
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        SecretStoreReadResult result = await secretStore
            .GetFileEncryptionSecretReadResultAsync()
            .ConfigureAwait(false);

        return new CredentialInventoryEntryPayload(
            ArcanumCredentialIdentity.FileEncryptionKeyAccount,
            "file-encryption",
            "File-encryption master key",
            "os-credential-store-with-encrypted-mirror",
            Status(result),
            result.Status == SecretStoreReadStatus.Ok ? "secure-store" : "none",
            null,
            FileEncryptionRecovery);

    }

    private async Task<CredentialInventoryEntryPayload> BuildWebResearchEntryAsync(
        CancellationToken cancellationToken)
    {

        WebBrowsingSettings webResearch = settings.Value.ResolveWebBrowsing();

        string environmentVariable =
            EnvironmentCredentialResolver.GetWebResearchApiKeyEnvironmentVariableName(webResearch);

        if (!string.IsNullOrWhiteSpace(
                EnvironmentCredentialResolver.ResolveWebResearchApiKey(webResearch)))
        {

            return new CredentialInventoryEntryPayload(
                ArcanumCredentialIdentity.PerplexityApiKeyAccount,
                "web-research",
                "Web research (Perplexity)",
                "environment-reference-or-secure-store",
                "configured",
                "environment",
                environmentVariable,
                WebResearchRecovery);

        }

        SecretStoreReadResult result = await webResearchCredentialStore
            .GetPerplexityApiKeyReadResultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new CredentialInventoryEntryPayload(
            ArcanumCredentialIdentity.PerplexityApiKeyAccount,
            "web-research",
            "Web research (Perplexity)",
            "environment-reference-or-secure-store",
            Status(result),
            result.Status == SecretStoreReadStatus.Ok ? "secure-store" : "none",
            environmentVariable,
            WebResearchRecovery);

    }

    private async Task<CredentialInventoryEntryPayload> BuildInferenceProviderEntryAsync(
        ProviderSettings provider,
        CancellationToken cancellationToken)
    {

        string environmentVariable =
            EnvironmentCredentialResolver.GetProviderApiKeyEnvironmentVariableName(provider);

        if (!string.IsNullOrWhiteSpace(
                EnvironmentCredentialResolver.ResolveProviderApiKey(provider)))
        {

            return new CredentialInventoryEntryPayload(
                ArcanumCredentialIdentity.InferenceProviderApiKeyAccount(provider.Name),
                "inference-provider",
                provider.Name,
                "environment-reference-or-secure-store",
                "configured",
                "environment",
                environmentVariable,
                ProviderRecovery);

        }

        SecretStoreReadResult result = await providerCredentialStore
            .GetApiKeyReadResultAsync(provider.Name, cancellationToken)
            .ConfigureAwait(false);

        return new CredentialInventoryEntryPayload(
            ArcanumCredentialIdentity.InferenceProviderApiKeyAccount(provider.Name),
            "inference-provider",
            provider.Name,
            "environment-reference-or-secure-store",
            Status(result),
            result.Status == SecretStoreReadStatus.Ok ? "secure-store" : "none",
            environmentVariable,
            ProviderRecovery);

    }

    private static string Status(SecretStoreReadResult result) =>
        result.Status switch
        {
            SecretStoreReadStatus.Ok when !string.IsNullOrWhiteSpace(result.Value) => "configured",
            SecretStoreReadStatus.Corrupted => "corrupt",
            _ => "missing",
        };

}
