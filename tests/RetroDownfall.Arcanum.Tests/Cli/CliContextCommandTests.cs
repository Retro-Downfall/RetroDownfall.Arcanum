using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
public sealed class CliContextCommandTests
{

    [Fact]
    public async Task Use_model_routes_the_identifier_to_the_context_service()
    {

        FakeCliContextService context = new();

        ServiceCollection services = CreateServices(context);

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["use", "model", "provider/model"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(CliContextScope.Model, context.SelectedScope);

        Assert.Equal("provider/model", context.SelectedIdentifier);

        Assert.Contains("provider/model", result.Output, StringComparison.Ordinal);

    }

    [Fact]

    public async Task Campaign_use_is_an_alias_for_shared_campaign_context_selection()
    {

        FakeCliContextService context = new();

        ServiceCollection services = CreateServices(context);

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["campaign", "use", "campaign-alpha"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(CliContextScope.Campaign, context.SelectedScope);

        Assert.Equal("campaign-alpha", context.SelectedIdentifier);

    }

    [Fact]
    public async Task Use_clear_without_scope_clears_every_saved_value()
    {

        FakeCliContextService context = new();

        ServiceCollection services = CreateServices(context);

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["use", "clear"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(CliContextScope.All, context.ClearedScope);

    }

    [Fact]
    public async Task Context_current_reports_value_sources_and_honors_no_context()
    {

        FakeCliContextService context = new()
        {
            Status = new CliContextStatusPayload(
                new CliContextStatusValue("campaign-alpha", "active context"),
                new CliContextStatusValue("/work/alpha", "active context"),
                new CliContextStatusValue("model-alpha", "active context"),
                new CliContextStatusValue("-", "server default"),
                [],
                "/state/cli-context.json"),
        };

        ServiceCollection services = CreateServices(context);

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["context", "current", "--no-context"]);

        Assert.Equal(0, result.ExitCode);

        Assert.True(context.LastNoContext);

        Assert.Contains("campaign-alpha", result.Output, StringComparison.Ordinal);

        Assert.Contains("active context", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Context_current_json_writes_one_typed_document()
    {

        FakeCliContextService context = new();

        ServiceCollection services = CreateServices(context);

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["context", "current", "--json"]);

        Assert.Equal(0, result.ExitCode);

        using JsonDocument json = JsonDocument.Parse(result.Output);

        Assert.Equal(
            "server default",
            json.RootElement
                .GetProperty("campaign")
                .GetProperty("source")
                .GetString());

        Assert.Equal(
            "/state/cli-context.json",
            json.RootElement.GetProperty("stateFile").GetString());

    }

    [Fact]
    public async Task Use_clear_with_invalid_scope_writes_one_json_error_document()
    {

        FakeCliContextService context = new();

        ServiceCollection services = CreateServices(context);

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["use", "clear", "unknown", "--json"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        using JsonDocument json = JsonDocument.Parse(result.Output);

        Assert.False(json.RootElement.GetProperty("isSuccess").GetBoolean());

        Assert.Contains(
            "Unknown context scope",
            json.RootElement.GetProperty("message").GetString(),
            StringComparison.Ordinal);

    }

    private static ServiceCollection CreateServices(ICliContextService context)
    {

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        services.AddSingleton(context);

        return services;

    }

    private sealed class FakeCliContextService : ICliContextService
    {

        public CliContextScope? SelectedScope { get; private set; }

        public string? SelectedIdentifier { get; private set; }

        public CliContextScope? ClearedScope { get; private set; }

        public bool LastNoContext { get; private set; }

        public CliContextStatusPayload Status { get; init; } =
            new(
                new CliContextStatusValue("-", "server default"),
                new CliContextStatusValue("-", "server default"),
                new CliContextStatusValue("-", "server default"),
                new CliContextStatusValue("-", "server default"),
                [],
                "/state/cli-context.json");

        public Task<CliContextMutationResult> SelectAsync(
            CliContextScope scope,
            string identifier,
            CancellationToken cancellationToken)
        {

            SelectedScope = scope;

            SelectedIdentifier = identifier;

            return Task.FromResult(
                CliContextMutationResult.Success($"Using {identifier}."));

        }

        public CliContextMutationResult Clear(CliContextScope scope)
        {

            ClearedScope = scope;

            return CliContextMutationResult.Success("Context cleared.");

        }

        public Task<CliContextStatusPayload> GetCurrentAsync(
            bool noContext,
            CancellationToken cancellationToken)
        {

            LastNoContext = noContext;

            return Task.FromResult(Status);

        }

    }

}
