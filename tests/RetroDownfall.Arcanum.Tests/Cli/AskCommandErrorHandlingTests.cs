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

    private sealed class ThrowingEye : IEyeOfTheWorld
    {

        public Task<PatternSnapshot> PerceivePatternAsync(string directoryPath, CancellationToken cancellationToken)
        {

            throw new InvalidOperationException("simulated eye failure");

        }

    }

}
