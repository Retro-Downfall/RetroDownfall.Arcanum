using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Hosting;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;

using Microsoft.ML.Tokenizers;

using RetroDownfall.Arcanum.Api;

using RetroDownfall.Arcanum.Api.Configuration;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class InferenceTokenizerStartupTests
{
    [Fact]
    public void Actual_startup_filter_initializes_default_before_pipeline_configuration()
    {
        TestCapturingLogger<InferenceTokenizerResolver> logger = new();

        using ServiceProvider services = CreateServices(logger);

        InferenceTokenizerResolver resolver = services.GetRequiredService<InferenceTokenizerResolver>();

        IStartupFilter filter = Assert.Single(services.GetServices<IStartupFilter>(), static value => value is ConfigurationStartupValidator);

        Assert.Empty(logger.Entries);

        bool configured = false;

        Action<IApplicationBuilder> pipeline = filter.Configure(_ =>
        {
            Assert.Single(logger.Entries);

            configured = true;
        });

        Assert.Single(logger.Entries);

        Tokenizer tokenizer = resolver.ResolveTokenizer(null);

        Assert.Equal(2, tokenizer.CountTokens("hello world"));

        Assert.Single(logger.Entries);

        pipeline(new ApplicationBuilder(services));

        Assert.True(configured);
    }

    [Fact]
    public void Startup_does_not_try_provider_model_names_or_initialize_other_requested_encodings()
    {
        TestCapturingLogger<InferenceTokenizerResolver> logger = new();

        using ServiceProvider services = CreateServices(logger);

        IStartupFilter filter = Assert.Single(services.GetServices<IStartupFilter>(), static value => value is ConfigurationStartupValidator);

        _ = filter.Configure(static _ => { });

        TestLogEntry created = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Debug, created.Level);

        Assert.Equal("o200k_base", Assert.Single(created.Properties!, static value => value.Key == "EncodingName").Value);

        InferenceTokenizerResolver resolver = services.GetRequiredService<InferenceTokenizerResolver>();

        ResolvedInferenceTokenizer fallback = resolver.Resolve("not-a-real-encoding-name");

        Assert.True(fallback.UsedFallback);

        Assert.Equal("not-a-real-encoding-name", fallback.RequestedEncoding);

        Assert.Equal("o200k_base", fallback.ActualEncoding);

        Assert.Same(resolver.ResolveTokenizer(null), fallback.Tokenizer);

        Assert.Single(logger.Entries, static entry => entry.Level == LogLevel.Warning);

        Assert.Single(logger.Entries, static entry => entry.Level == LogLevel.Debug);
    }

    [Fact]
    public void Invalid_configuration_refuses_startup_before_tokenizer_initialization()
    {
        TestCapturingLogger<InferenceTokenizerResolver> logger = new();

        using ServiceProvider services = CreateServices(logger, invalid: true);

        IStartupFilter filter = Assert.Single(services.GetServices<IStartupFilter>(), static value => value is ConfigurationStartupValidator);

        Assert.Throws<ConfigurationValidationException>(() => filter.Configure(static _ => { }));

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Concurrent_cold_default_resolution_initializes_the_real_tokenizer_once()
    {
        TestCapturingLogger<InferenceTokenizerResolver> logger = new();

        InferenceTokenizerResolver resolver = new(logger);

        using Barrier ready = new(4);

        Task<Tokenizer>[] requests = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
        {
            Assert.True(ready.SignalAndWait(TimeSpan.FromSeconds(30)));

            return resolver.ResolveTokenizer(null);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        Tokenizer[] tokenizers = await Task.WhenAll(requests);

        Assert.All(tokenizers, tokenizer => Assert.Same(tokenizers[0], tokenizer));

        Assert.All(tokenizers, static tokenizer => Assert.Equal(2, tokenizer.CountTokens("hello world")));

        Assert.Single(logger.Entries);
    }

    [Fact]
    public void Unknown_encoding_remains_a_cached_fallback_without_startup()
    {
        TestCapturingLogger<InferenceTokenizerResolver> logger = new();

        InferenceTokenizerResolver resolver = new(logger);

        ResolvedInferenceTokenizer first = resolver.Resolve("not-a-real-encoding-name");

        ResolvedInferenceTokenizer second = resolver.Resolve("not-a-real-encoding-name");

        Assert.Same(first, second);

        Assert.True(first.UsedFallback);

        Assert.Equal(2, first.Tokenizer.CountTokens("hello world"));

        Assert.Same(resolver.ResolveTokenizer(null), first.Tokenizer);

        Assert.Single(logger.Entries, static entry => entry.Level == LogLevel.Warning);
    }

    private static ServiceProvider CreateServices(TestCapturingLogger<InferenceTokenizerResolver> logger, bool invalid = false)
    {
        ConfigurationBuilder configurationBuilder = new();

        configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Arcanum:Providers:0:Name"] = "local-test-provider",

            ["Arcanum:Providers:0:Type"] = "OpenAICompatible",

            ["Arcanum:Providers:0:Models:0"] = "not-an-encoding-model",

            ["Arcanum:DefaultModel"] = "not-an-encoding-model",
        });

        if (invalid)
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:LlamaCpp:ServerExecutablePath"] = "/not-used",
            });
        }

        IConfiguration configuration = configurationBuilder.Build();

        ServiceCollection services = [];

        services.AddSingleton(configuration);

        services.AddLogging();

        services.AddArcanumApiServices(configuration);

        services.AddSingleton<ILogger<InferenceTokenizerResolver>>(logger);

        return services.BuildServiceProvider();
    }
}
