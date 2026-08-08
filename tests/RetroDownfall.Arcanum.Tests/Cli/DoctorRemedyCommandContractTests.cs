using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Every command a diagnostic can tell an operator to run must actually exist.
///
/// <para>Issue #33 names this directly: the pre-existing guidance pointed at
/// <c>arcanum config set-key</c>, a verb that has never existed in this tree, so an operator whose
/// credential had expired was sent to a command that would only print a parse error. A remediation
/// that cannot be run is worse than none, because it costs the reader their remaining trust in the
/// report. Parsing each constant against the real command tree makes a renamed or removed verb a
/// build failure rather than shipped bad advice.</para>
/// </summary>
[Collection("GlobalConsole")]
public sealed class DoctorRemedyCommandContractTests : IDisposable
{

    private readonly string _testHome =
        Path.Combine(Path.GetTempPath(), "arcanum-tests", $"doctor-remedy-{Guid.NewGuid():N}");

    private readonly Dictionary<string, string?> _originalEnvironment = new();

    public DoctorRemedyCommandContractTests()
    {

        Directory.CreateDirectory(_testHome);

        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Testing");

        SetEnvironment("DOTNET_ENVIRONMENT", "Testing");

        SetEnvironment("ARCANUM_TEST_HOME", _testHome);

    }

    [Fact]
    public async Task Every_remediation_command_resolves_against_the_real_command_tree()
    {

        List<string> unresolved = [];

        foreach (string command in DoctorRemedyCommands.All)
        {

            string[] verbs = VerbPath(command);

            CliTestResult result = await CliTestHarness.RunAsync(
                BuildServices(),
                [.. verbs, "--help"]);

            // An unknown verb never reaches a handler, so the parser reports it on stderr and exits
            // with the closed configuration-error code. Help for a real verb always succeeds.
            if (result.ExitCode != (int)CliExitCode.Success)
            {

                unresolved.Add($"{command} (exit {result.ExitCode}: {result.Error.Trim()})");

            }

        }

        Assert.Empty(unresolved);

    }

    [Fact]
    public void Every_remediation_command_is_an_arcanum_command()
    {

        Assert.All(
            DoctorRemedyCommands.All,
            command => Assert.StartsWith("arcanum ", command, StringComparison.Ordinal));

    }

    [Fact]
    public void No_remediation_command_names_a_verb_this_tree_never_had()
    {

        // The exact obsolete spelling issue #33 calls out. Pinned so it cannot creep back in.
        Assert.DoesNotContain(
            DoctorRemedyCommands.All,
            command => command.Contains("set-key", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public void A_provider_scoped_credential_remedy_names_the_provider_instead_of_a_placeholder()
    {

        string command = DoctorRemedyCommands.KeyProviderSetFor("openai");

        Assert.Equal("arcanum key provider set openai", command);

        Assert.DoesNotContain('<', command);

    }

    /// <summary>
    /// The verb path only: leading <c>arcanum</c>, every <c>&lt;placeholder&gt;</c>, and everything
    /// from the first option onward are dropped, because this asserts the command exists, not that a
    /// particular operand is valid.
    /// </summary>
    private static string[] VerbPath(string command)
    {

        List<string> verbs = [];

        foreach (string token in command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {

            if (token.StartsWith('-') || token.StartsWith('<'))
            {

                break;

            }

            verbs.Add(token);

        }

        return [.. verbs];

    }

    private static ServiceCollection BuildServices()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.AddSingleton<ISecretStore>(new NullSecretStore());

        return services;

    }

    private sealed class NullSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

    }

    public void Dispose()
    {

        foreach (KeyValuePair<string, string?> entry in _originalEnvironment)
        {

            global::System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);

        }

        if (Directory.Exists(_testHome))
        {

            Directory.Delete(_testHome, recursive: true);

        }

    }

    private void SetEnvironment(string name, string value)
    {

        _originalEnvironment[name] = global::System.Environment.GetEnvironmentVariable(name);

        global::System.Environment.SetEnvironmentVariable(name, value);

    }

}
