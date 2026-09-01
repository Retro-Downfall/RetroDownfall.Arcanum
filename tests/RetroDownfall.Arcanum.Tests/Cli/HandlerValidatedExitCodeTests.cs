using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// W10-8: a handler-validated required/parse failure (Command.Reference.md:96-115 — nullable in the
/// generated parser so the handler can resolve saved context, read a secure value, or produce a
/// better error) must exit the same way <c>run</c> already does for the same class of failure —
/// <see cref="CliExitCode.ConfigurationError"/> — everywhere this packet owns a handler, not exit 1
/// on some verbs and exit 2 on others.
/// </summary>
[Trait("Category", "Integration")]
[Collection("GlobalConsole")]
public sealed class HandlerValidatedExitCodeTests
{

    public static TheoryData<string[], string> Cases => new()
    {
        { ["prompt", "create"], "prompt create --name" },
        { ["spell", "delete", "greet"], "spell delete --workspace" },
        { ["spell", "execute", "greet"], "spell execute --input" },
        { ["campaign", "create"], "campaign create --name/--path" },
        { ["campaign", "codex", "put", System.Guid.NewGuid().ToString()], "campaign codex put --file" },
        { ["apprentice", "create"], "apprentice create --goal" },
        { ["session", "rename", System.Guid.NewGuid().ToString()], "session rename --title" },
        { ["prompt", "list", "--campaign-id", "not-a-guid"], "prompt list --campaignId parse failure" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Handler_validated_failure_exits_ConfigurationError(string[] args, string label)
    {

        CliTestResult result = RunCommand(args);

        Assert.True(
            (int)CliExitCode.ConfigurationError == result.ExitCode,
            $"{label}: expected exit {(int)CliExitCode.ConfigurationError} (ConfigurationError), got {result.ExitCode}. stderr: {result.Error}");

    }

    private static CliTestResult RunCommand(string[] args)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FakeSecretStore());

        return CliTestHarness.Run(services, args);

    }

    private sealed class FakeSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>("test-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok("test-key"));

        public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

    }

}
