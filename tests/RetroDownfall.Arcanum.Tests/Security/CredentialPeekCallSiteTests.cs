using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// Keeps read-only credential probes on the non-mutating read contract and authenticated thin
/// clients on the server-verified credential lease. Behavioral tests prove both paths avoid
/// migration-capable reads while this inventory prevents callers from silently bypassing them.
/// </summary>
public sealed class CredentialPeekCallSiteTests
{
    public static TheoryData<string, string, string> ReadOnlyCallSites =>
        new()
        {
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

    [Theory]
    [InlineData("src/RetroDownfall.Arcanum.Cli/Services/FileBatchApiClient.cs")]
    [InlineData("src/RetroDownfall.Arcanum.Cli/Services/ArcanumServeLauncher.cs")]
    [InlineData("src/RetroDownfall.Arcanum.Cli/Diagnostics/HostHealthDiagnostics.cs")]
    public void Authenticated_thin_clients_use_the_server_verified_credential_lease(
        string relativePath)
    {
        ProductionSource source = Assert.Single(
            ProductionSourceInventory.Sources(),
            candidate => candidate.IsExactOwner(relativePath));

        Assert.Contains("ArcanumApiCredentialLease", source.Text, StringComparison.Ordinal);

        string compact = string.Join(
            ' ',
            source.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        Assert.True(
            source.Text.Contains(".ResolveAsync(", StringComparison.Ordinal)
            || source.Text.Contains(".ProbeAuthenticatedAsync(", StringComparison.Ordinal)
            || compact.Contains(
                "ArcanumAuthenticatedHttpSender.SendAsync( client, credentialLease,",
                StringComparison.Ordinal));

        Assert.DoesNotContain("ISecretStore", source.Text, StringComparison.Ordinal);

        Assert.DoesNotContain("PeekApiKeyReadResultAsync", source.Text, StringComparison.Ordinal);

        Assert.DoesNotContain("GetApiKeyAsync", source.Text, StringComparison.Ordinal);

        Assert.DoesNotContain("GetApiKeyReadResultAsync", source.Text, StringComparison.Ordinal);
    }
}
