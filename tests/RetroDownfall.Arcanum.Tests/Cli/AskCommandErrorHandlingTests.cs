using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
public sealed class AskCommandErrorHandlingTests
{

    [Fact]
    public void Ask_yields_exit_one_when_turn_throws_unexpected_exception()
    {

        // An unexpected (non-OCE) fault inside the ask turn must surface as a formatted
        // error + exit 1, not as an unhandled exception / raw stack trace. This exercises
        // the real Program.Main wiring (CliApplicationFactory.RunAsync) with the eye
        // replaced by a throwing fake so the fault lands inside the ask turn body.
        //
        // Note: ConfigureCliServices does not register IApiKeyDigestCache (a pre-existing
        // CLI DI wiring gap tracked separately from W2.3); it is registered here so the
        // command can be constructed and the turn-body catch exercised.

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();

        services.AddSingleton<IEyeOfTheWorld, ThrowingEye>();

        CliTestResult result = CliTestHarness.Run(services, "run", "hello");

        Assert.Equal(1, result.ExitCode);

    }

    /// <summary>
    /// W10-6: the bare <c>catch (Exception ex)</c> around the ask turn used to print
    /// <c>ex.Message</c> verbatim, so an <see cref="IOException"/> naming a local path reached the
    /// operator's stderr unredacted, and the exit code was a hardcoded 1 rather than
    /// <see cref="CliFailureMapper"/>'s classification. Routed through the mapper, only its safe
    /// copy and exit code reach the console; the raw message is confined to <c>-v</c> output.
    /// </summary>
    [Fact]
    public void Ask_routes_unexpected_exceptions_through_the_safe_failure_mapper()
    {

        const string LeakedPath = "/Users/x/.arcanum/secret.bin";

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();

        services.AddSingleton<IEyeOfTheWorld>(
            new ThrowingEyeWith(new IOException($"{LeakedPath} denied")));

        CliTestResult result = CliTestHarness.Run(services, "run", "hello");

        CliFailure expected = CliFailureMapper.Map(new IOException($"{LeakedPath} denied"));

        Assert.Equal((int)expected.ExitCode, result.ExitCode);

        Assert.DoesNotContain(LeakedPath, result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain("IOException", result.Error, StringComparison.Ordinal);

        Assert.Contains(expected.SafeMessage, result.Error, StringComparison.Ordinal);

    }

    /// <summary>
    /// W10-6 mutation-strength gap: the test above throws an <see cref="IOException"/>, which
    /// <see cref="CliFailureMapper"/> classifies as <see cref="CliExitCode.GenericError"/> — the same
    /// numeric value (1) the old hardcoded <c>return 1;</c> produced, so reverting to that literal
    /// while still constructing a <c>SafeMessage</c> from somewhere would leave that test green. An
    /// exception the mapper classifies to a <em>different</em> code closes that gap: this only passes
    /// if the exit code actually comes from the mapper's classification.
    /// </summary>
    [Fact]
    public void Ask_maps_a_network_exception_to_the_network_exit_code()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();

        services.AddSingleton<IEyeOfTheWorld>(
            new ThrowingEyeWith(new HttpRequestException("Connection refused")));

        CliTestResult result = CliTestHarness.Run(services, "run", "hello");

        CliFailure expected = CliFailureMapper.Map(new HttpRequestException("Connection refused"));

        Assert.Equal((int)expected.ExitCode, result.ExitCode);

        Assert.Contains(expected.SafeMessage, result.Error, StringComparison.Ordinal);

    }

    private sealed class ThrowingEye : IEyeOfTheWorld
    {

        public Task<PatternSnapshot> PerceivePatternAsync(string directoryPath, CancellationToken cancellationToken)
        {

            throw new InvalidOperationException("simulated eye failure");

        }

    }

    private sealed class ThrowingEyeWith(Exception exception) : IEyeOfTheWorld
    {

        public Task<PatternSnapshot> PerceivePatternAsync(string directoryPath, CancellationToken cancellationToken) =>
            throw exception;

    }

}
