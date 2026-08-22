using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// Keeps read-only and thin-client credential probes on the non-mutating read contract. Behavioral
/// tests for the stores prove Peek performs no OS or mirror writes; this inventory prevents a
/// consumer from silently switching back to the migration-capable read.
/// </summary>
public sealed class CredentialPeekCallSiteTests
{

    public static TheoryData<string, string, string> ReadOnlyCallSites =>
        new()
        {
            {
                "src/RetroDownfall.Arcanum.Cli/Services/FileBatchApiClient.cs",
                ".PeekApiKeyReadResultAsync()",
                ".GetApiKeyAsync()"
            },
            {
                "src/RetroDownfall.Arcanum.Cli/Services/ArcanumServeLauncher.cs",
                ".PeekApiKeyReadResultAsync()",
                ".GetApiKeyAsync()"
            },
            {
                "src/RetroDownfall.Arcanum.Cli/Diagnostics/HostHealthDiagnostics.cs",
                ".PeekApiKeyReadResultAsync()",
                ".GetApiKeyAsync()"
            },
            {
                "src/RetroDownfall.Arcanum.Cli/Commands/KeyCommands.cs",
                ".PeekApiKeyReadResultAsync()",
                ".GetApiKeyReadResultAsync()"
            },
            {
                "src/RetroDownfall.Arcanum.Cli/Commands/KeyCommands.cs",
                ".PeekFileEncryptionSecretReadResultAsync()",
                ".GetFileEncryptionSecretReadResultAsync()"
            },
            {
                "src/RetroDownfall.Arcanum.Cli/Commands/KeyCommands.cs",
                ".PeekPerplexityApiKeyReadResultAsync(cancellationToken)",
                ".GetPerplexityApiKeyReadResultAsync(cancellationToken)"
            },
            {
                "src/RetroDownfall.Arcanum.Cli/Commands/KeyCommands.cs",
                ".PeekApiKeyReadResultAsync(provider.Name, cancellationToken)",
                ".GetApiKeyReadResultAsync(provider.Name, cancellationToken)"
            },
            {
                "src/RetroDownfall.Arcanum.Cli/Commands/DoctorCommand.cs",
                ".PeekApiKeyReadResultAsync()",
                ".GetApiKeyAsync()"
            },
            {
                "src/RetroDownfall.Arcanum.Cli/Commands/DoctorCommand.cs",
                ".PeekApiKeyReadResultAsync(provider.Name, cancellationToken)",
                ".GetApiKeyReadResultAsync(provider.Name, cancellationToken)"
            },
            {
                "src/RetroDownfall.Arcanum.Cli/Commands/DoctorCommand.cs",
                ".PeekPerplexityApiKeyReadResultAsync(cancellationToken)",
                ".GetPerplexityApiKeyReadResultAsync(cancellationToken)"
            },
            {
                "src/RetroDownfall.Arcanum.Cli/Diagnostics/ProviderDiagnostics.cs",
                ".PeekPerplexityApiKeyReadResultAsync(cancellationToken)",
                ".GetPerplexityApiKeyReadResultAsync(cancellationToken)"
            },
            {
                "src/RetroDownfall.Arcanum.Cli/Diagnostics/ProviderDiagnostics.cs",
                ".PeekAsync(provider, cancellationToken)",
                ".ResolveAsync(provider, cancellationToken)"
            },
            {
                "src/RetroDownfall.Arcanum.Api/Health/ArcanumHealthChecker.cs",
                ".PeekAsync(provider, cancellationToken)",
                ".ResolveAsync(provider, cancellationToken)"
            },
            {
                "src/RetroDownfall.Arcanum.Infrastructure/Storage/EncryptedBlobDiagnostics.cs",
                ".PeekFileEncryptionSecretReadResultAsync()",
                ".GetFileEncryptionSecretReadResultAsync()"
            },
            {
                "src/RetroDownfall.Arcanum.Infrastructure/Diagnostics/GrimoireDiagnostics.cs",
                ".PeekApiKeyReadResultAsync()",
                ".GetApiKeyAsync()"
            },
            {
                "src/RetroDownfall.Arcanum.Cli/Services/Setup/SetupPlanner.cs",
                ".PeekApiKeyReadResultAsync(probe.Name, cancellationToken)",
                ".GetApiKeyReadResultAsync(probe.Name, cancellationToken)"
            },
            {
                "src/RetroDownfall.Arcanum.Cli/Services/Setup/SetupPlanner.cs",
                ".PeekPerplexityApiKeyReadResultAsync(cancellationToken)",
                ".GetPerplexityApiKeyReadResultAsync(cancellationToken)"
            },
            {
                "src/RetroDownfall.Arcanum.Cli/Commands/SetupCommand.cs",
                ".PeekAsync(",
                ".ResolveAsync("
            },
            {
                "src/RetroDownfall.Arcanum.Api/Security/ApiKeyAuthenticator.cs",
                ".PeekApiKeyReadResultAsync()",
                ".GetApiKeyAsync()"
            },
            {
                "src/RetroDownfall.Compendium.Ux/Services/FamiliarProbeClient.cs",
                ".PeekApiKeyReadResultAsync()",
                ".GetApiKeyAsync()"
            },
        };

    [Theory]
    [MemberData(nameof(ReadOnlyCallSites))]
    public void Read_only_credential_callsites_use_peek(
        string relativePath,
        string requiredCall,
        string forbiddenCall)
    {

        ProductionSource source = Assert.Single(
            ProductionSourceInventory.Sources(),
            candidate => candidate.IsExactOwner(relativePath));

        Assert.Contains(requiredCall, source.Text, StringComparison.Ordinal);

        Assert.DoesNotContain(forbiddenCall, source.Text, StringComparison.Ordinal);

    }

}
