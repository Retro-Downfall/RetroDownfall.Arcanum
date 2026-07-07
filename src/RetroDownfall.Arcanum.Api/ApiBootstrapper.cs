using System.Diagnostics;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using RetroDownfall.Arcanum.Api.A2A;
using RetroDownfall.Arcanum.Api.Middleware;
using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Api.Configuration;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Guardrails;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Api.Daemons;
using RetroDownfall.Arcanum.Api.Mcp;
using RetroDownfall.Arcanum.Api.Perception;
using RetroDownfall.Arcanum.Api.ProvingGrounds;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Spells;
using RetroDownfall.Arcanum.Api.Telemetry;
using RetroDownfall.Arcanum.Api.Workspaces;
using RetroDownfall.Arcanum.Api.LlamaCpp;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.Arcanum.Core.Telemetry;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.TheForge;
using Scalar.AspNetCore;

namespace RetroDownfall.Arcanum.Api;

public static class ApiBootstrapper
{
    private const string ArcanumCorsPolicyName = "ArcanumCors";

    internal const string ArcanumRateLimiterPolicyName = "ArcanumRateLimit";

    private static readonly string[] DefaultCorsAllowedOrigins = new HostSettings().CorsAllowedOrigins;

    private static void RegisterRateLimiter(IServiceCollection services, IConfiguration configuration)
    {
        if (!IsRateLimitEnabled(configuration))
        {
            return;
        }

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // W3.5: emit the uniform ApiResponse envelope on rejection (explicit JsonTypeInfo, AOT-safe)
            // instead of a bare 429 with no body, matching every other /api and /v1 route.
            options.OnRejected = static async (context, cancellationToken) =>
            {
                HttpContext http = context.HttpContext;

                if (http.Response.HasStarted)
                {
                    return;
                }

                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                string traceId = Activity.Current?.Id ?? http.TraceIdentifier;

                Result<string> rejected = Result<string>.Failure(
                    new Error(ErrorCodes.RateLimit.TooManyRequests, "Too many requests; please slow down and retry."));

                ApiResponse<string> envelope = ApiResponse<string>.FromResult(rejected, traceId);

                await http.Response.WriteAsJsonAsync(
                    envelope,
                    ArcanumJsonContext.Default.ApiResponseString,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            };

            options.AddPolicy(ArcanumRateLimiterPolicyName, ctx =>
            {
                IOptionsMonitor<ArcanumSettings> monitor = ctx.RequestServices
                    .GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

                HostRateLimitSettings rl = monitor.CurrentValue.Host?.RateLimit ?? new HostRateLimitSettings();

                int permitLimit = ArcanumSettingClamps.RateLimitPermitLimit(rl.PermitLimit);

                int windowSeconds = ArcanumSettingClamps.RateLimitWindowSeconds(rl.WindowSeconds);

                int queueLimit = ArcanumSettingClamps.RateLimitQueueLimit(rl.QueueLimit);

                string partitionKey = ResolveRateLimitPartitionKey(ctx);

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = TimeSpan.FromSeconds(windowSeconds),
                        QueueLimit = queueLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true,
                    });
            });
        });
    }

    private static string ResolveRateLimitPartitionKey(HttpContext context)
    {
        System.Net.IPAddress? ip = context.Connection.RemoteIpAddress;

        return "ip:" + (ip?.ToString() ?? "unknown");
    }

    /// <summary>
    /// Gzip + Brotli response compression for large JSON responses (Kestrel built-in — no new NuGet
    /// dependency). <see cref="ResponseCompressionOptions.EnableForHttps"/> is left at its framework
    /// default (<see langword="false"/>) — Arcanum typically serves over loopback HTTP, or behind a
    /// reverse proxy that terminates TLS and can compress itself; the default conservatively avoids
    /// any BREACH/CRIME-style compression-oracle risk on an HTTPS listener until there is a concrete
    /// need for it. <see cref="ResponseCompressionDefaults.MimeTypes"/> already excludes
    /// <c>text/event-stream</c> and <c>application/x-ndjson</c>, but they are explicitly subtracted
    /// here too so streaming endpoints (SSE, NDJSON) stay uncompressed by contract, not by accident of
    /// today's defaults — compressing an incrementally-flushed stream would buffer frames and defeat
    /// the anti-buffering headers those endpoints already set (§8.9).
    /// </summary>
    private static void RegisterResponseCompression(IServiceCollection services)
    {
        services.AddResponseCompression(options =>
        {
            options.Providers.Add<BrotliCompressionProvider>();

            options.Providers.Add<GzipCompressionProvider>();

            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Except(
                ["text/event-stream", "application/x-ndjson"],
                StringComparer.OrdinalIgnoreCase);
        });
    }

    private static bool ReadConfiguredListenAny(IConfiguration configuration)
    {
        string? configured = configuration["Arcanum:Host:ListenAny"];

        if (string.IsNullOrWhiteSpace(configured))
        {
            return new HostSettings().ListenAny;
        }

        return bool.TryParse(configured.Trim(), out bool parsed) && parsed;
    }

    private static bool IsRateLimitEnabled(IConfiguration configuration)
    {
        bool explicitlyEnabled = string.Equals(
            configuration["Arcanum:Host:RateLimit:Enabled"]?.Trim(),
            bool.TrueString,
            StringComparison.OrdinalIgnoreCase);

        return ArcanumEnvironment.IsRateLimitEnabled(explicitlyEnabled, ReadConfiguredListenAny(configuration));
    }

    /// <summary>
    /// Effective <c>/metrics</c> auth gate. <c>Arcanum:Metrics:RequireApiKey</c> opts in explicitly; an
    /// all-interfaces bind (<c>Arcanum:Host:ListenAny</c> / <c>ARCANUM_HOST_ANY</c>) forces the gate on
    /// regardless of that setting — the same zero-trust downgrade pattern applied to CORS wildcards
    /// (<see cref="AddArcanumApiServices"/>) and the rate limiter (<see cref="IsRateLimitEnabled"/>).
    /// </summary>
    private static bool IsMetricsRequireApiKeyEffective(IConfiguration configuration)
    {
        bool explicitlyRequired = string.Equals(
            configuration["Arcanum:Metrics:RequireApiKey"]?.Trim(),
            bool.TrueString,
            StringComparison.OrdinalIgnoreCase);

        return explicitlyRequired || ArcanumEnvironment.IsHostAnyEnabled(ReadConfiguredListenAny(configuration));
    }

    private static string[] ReadCorsAllowedOriginsFromConfiguration(IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection("Arcanum:Host:CorsAllowedOrigins");

        if (!section.Exists())
        {
            return DefaultCorsAllowedOrigins;
        }

        List<string> values = [];

        foreach (IConfigurationSection child in section.GetChildren())
        {
            string? value = child.Value;

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            values.Add(value.Trim().TrimEnd('/'));
        }

        return values.ToArray();
    }

    public static IServiceCollection AddArcanumApiServices(this IServiceCollection services, IConfiguration configuration)
    {

        services.AddExceptionHandler<ArcanumExceptionHandler>();

        services.AddProblemDetails();

        services.AddSingleton<ArcanumHealthChecker>();

        services.AddScoped<GrimoireStatsService>();

        services.AddSingleton<IHumanPromptRegistry, HumanPromptRegistry>();

        services.AddArcanumInfrastructure(configuration);

        services.AddArcanumDaemonServices(configuration);

        services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, ConfigurationStartupValidator>();

        services.AddSingleton<ApiKeyEndpointFilter>();

        services.AddCors(options =>
        {
            options.AddPolicy(
                ArcanumCorsPolicyName,
                policy =>
                {
                    string[] origins = ReadCorsAllowedOriginsFromConfiguration(configuration);

                    bool wildcard = origins.Any(static o => string.Equals(o, "*", StringComparison.Ordinal));

                    bool listenAny = ArcanumEnvironment.IsHostAnyEnabled(
                        configuration.GetValue<bool?>("Arcanum:Host:ListenAny") ?? false);

                    if (wildcard && listenAny)
                    {

                        origins = DefaultCorsAllowedOrigins;

                        wildcard = false;

                    }

                    if (wildcard)
                    {
                        policy.AllowAnyOrigin();
                    }
                    else if (origins.Length == 0)
                    {
                        policy.WithOrigins(DefaultCorsAllowedOrigins);
                    }
                    else
                    {
                        policy.WithOrigins(origins);
                    }

                    policy.WithMethods(
                        HttpMethods.Get,
                        HttpMethods.Post,
                        HttpMethods.Put,
                        HttpMethods.Delete,
                        HttpMethods.Patch,
                        HttpMethods.Head,
                        HttpMethods.Options);

                    policy.WithHeaders(
                        HeaderNames.ContentType,
                        HeaderNames.Accept,
                        HeaderNames.Authorization,
                        ArcanumApiHeaders.ApiKey,
                        HeaderNames.CacheControl,
                        HeaderNames.IfNoneMatch,
                        "X-Requested-With");
                });
        });

        services.AddOpenApi();

        RegisterRateLimiter(services, configuration);

        RegisterResponseCompression(services);

        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, ArcanumJsonContext.Default));

        services.AddTransient<OpenAiRequestAugmentingHandler>();

        services.AddTransient<LlamaCppRequestAugmentingHandler>();

        services.AddHttpClient(
            "OpenAiCompatibleProvider",
            static (sp, client) =>
            {
                _ = sp;

                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .AddHttpMessageHandler<OpenAiRequestAugmentingHandler>();

        services.AddSingleton<IChatClientFactory, ChatClientFactory>();

        services.AddSingleton<IEmbeddingGeneratorFactory, EmbeddingGeneratorFactory>();

        services.AddSingleton<IWeaveService, WeaveService>();

        services.AddSingleton<InferenceTokenizerResolver>();

        services.AddSingleton<StructuredOutputValidator>();

        services.AddSingleton<BudgetMonitor>();

        services.AddSingleton<ManaPreflight>();

        services.AddSingleton<IManaMeter, TheForge.ManaMeter>();

        services.AddSingleton<PromptRenderer>();

        services.AddScoped<GrimoireTurnWriter>();

        services.AddScoped<IContextCompressionService, ContextCompressionService>();

        services.AddScoped<InferenceContextBuilder>();

        services.AddScoped<ToolExecutionPipeline>();

        services.AddScoped<SemanticSpellRouter>();

        services.AddSingleton<GuardrailsPipeline>();

        services.AddScoped<IArcanumIntelligenceProvider, WizardIntelligenceProvider>();

        services.AddScoped<IBuiltInToolRegistry, BuiltInToolRegistry>();

        services.AddSingleton<BatchProcessingService>();

        services.AddHostedService(static sp => sp.GetRequiredService<BatchProcessingService>());

        services.AddScoped<IProvingGroundsArbiter, ProvingGroundsArbiter>();

        services.AddScoped<ProvingGroundsRunner>();

        services.AddSingleton<SpellWorkspaceResolver>();

        return services;
    }

    /// <summary>
    /// Activates centralized exception handling for all Arcanum API hosts.
    /// </summary>
    public static void UseArcanumExceptionHandler(this WebApplication app)
    {

        app.UseExceptionHandler();

    }

    /// <summary>
    /// Activates Gzip/Brotli response compression. Placed early in the pipeline (right after the
    /// exception handler, before CORS/rate limiting/endpoints) so it wraps everything — including
    /// error responses — matching ASP.NET Core's own middleware-ordering guidance for
    /// <c>UseResponseCompression</c>.
    /// </summary>
    public static void UseArcanumResponseCompression(this WebApplication app)
    {

        app.UseResponseCompression();

    }

    /// <summary>
    /// Applies the configurable Arcanum CORS policy from <c>Arcanum:Host:CorsAllowedOrigins</c>.
    /// Default is localhost loopback; use <c>["*"]</c> for permissive (browser-callable from any origin).
    /// </summary>
    public static void UseArcanumCors(this WebApplication app)
    {
        app.UseCors(ArcanumCorsPolicyName);
    }

    /// <summary>
    /// Activates the Arcanum rate-limiter middleware when rate limiting is effective
    /// (<c>Arcanum:Host:RateLimit:Enabled</c> or all-interfaces bind via ListenAny / <c>ARCANUM_HOST_ANY</c>);
    /// otherwise no-op (zero overhead).
    /// </summary>
    public static void UseArcanumRateLimiter(this WebApplication app)
    {
        if (IsRateLimitEnabled(app.Configuration))
        {
            app.UseRateLimiter();
        }
    }

    /// <summary>
    /// Records <c>arcanum_http_requests_total</c> for every request. Uses the matched route pattern
    /// (for example <c>/api/sessions/{id}/stream</c>), not the raw request path, to keep the
    /// <c>endpoint</c> label bounded — a raw URL would leak unbounded identifiers (session ids, etc.) as
    /// label values. Requests to <c>/metrics</c> itself are skipped so a Prometheus scraper cannot create
    /// a self-referential feedback loop (every scrape incrementing the very counter it just read).
    /// </summary>
    public static void UseArcanumMetrics(this WebApplication app)
    {
        app.Use(async (HttpContext context, Func<Task> next) =>
        {

            if (context.Request.Path.StartsWithSegments("/metrics"))
            {

                await next().ConfigureAwait(false);

                return;

            }

            await next().ConfigureAwait(false);

            string routeLabel = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
                ?? context.Request.Path.Value
                ?? "unknown";

            ArcanumMetrics.HttpRequestsTotal.Add(
                1,
                new KeyValuePair<string, object?>("endpoint", routeLabel),
                new KeyValuePair<string, object?>("method", context.Request.Method),
                new KeyValuePair<string, object?>("status_code", context.Response.StatusCode.ToString(CultureInfo.InvariantCulture)));

        });
    }

    public static void MapArcanumEndpoints(this WebApplication app)
    {
        bool rateLimitEnabled = IsRateLimitEnabled(app.Configuration);

        RouteGroupBuilder openAiV1 = app.MapGroup("/v1").AddEndpointFilter<ApiKeyEndpointFilter>();

        if (rateLimitEnabled)
        {
            openAiV1 = openAiV1.RequireRateLimiting(ArcanumRateLimiterPolicyName);
        }

        openAiV1.MapOpenAiV1ChatCompletions();

        openAiV1.MapOpenAiV1Embeddings();

        openAiV1.MapOpenAiV1Models();

        openAiV1.MapOpenAiV1Moderations();

        openAiV1.MapOpenAiV1ImagesStubs();

        openAiV1.MapOpenAiV1AudioStubs();

        openAiV1.MapOpenAiV1Files();

        openAiV1.MapOpenAiV1Batches();

        var apiGroup = app.MapGroup("/api").AddEndpointFilter<ApiKeyEndpointFilter>();

        if (rateLimitEnabled)
        {
            apiGroup = apiGroup.RequireRateLimiting(ArcanumRateLimiterPolicyName);
        }

        // /metrics lives outside /api and /v1 by default so Prometheus scrapers work without custom
        // headers; it is force-routed onto apiGroup instead (inheriting ApiKeyEndpointFilter and any
        // active rate limiter) when Arcanum:Metrics:RequireApiKey is set or the host binds all interfaces.
        if (IsMetricsRequireApiKeyEffective(app.Configuration))
        {
            apiGroup.MapMetricsEndpoint();
        }
        else
        {
            app.MapMetricsEndpoint();
        }

        apiGroup.MapOpenApi();

        bool enableScalar = string.Equals(
            app.Configuration["Arcanum:Host:EnableScalarUi"]?.Trim(),
            bool.TrueString,
            StringComparison.OrdinalIgnoreCase);

        if (enableScalar)
        {
            RouteGroupBuilder scalarGroup = apiGroup
                .MapGroup(string.Empty)
                .AddEndpointFilter(static async (EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
                {
                    object? result = await next(context).ConfigureAwait(false);

                    HttpResponse response = context.HttpContext.Response;

                    if (!response.Headers.ContainsKey("Content-Security-Policy"))
                    {
                        response.Headers.Append(
                            "Content-Security-Policy",
                            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'");
                    }

                    if (!response.Headers.ContainsKey("X-Content-Type-Options"))
                    {
                        response.Headers.Append("X-Content-Type-Options", "nosniff");
                    }

                    return result;
                });

            scalarGroup.MapScalarApiReference();
        }

        apiGroup.MapHealthEndpoints();

        apiGroup.MapBudgetEndpoints();

        apiGroup.MapLlamaEndpoints();

        apiGroup.MapConfigurationEndpoints();

        apiGroup.MapIntelligenceEndpoints();

        apiGroup.MapToolInvokeEndpoints();

        apiGroup.MapAuditEndpoints();

        apiGroup.MapGuardrailsAuditEndpoints();

        apiGroup.MapMcpEndpoints();

        apiGroup.MapPerceptionEndpoints();

        apiGroup.MapLoreEndpoints();

        apiGroup.MapSagaEndpoints();

        apiGroup.MapSpellEndpoints();

        apiGroup.MapSpellForgeEndpoints();

        apiGroup.MapSpellExecutionEndpoints();

        apiGroup.MapCampaignEndpoints();

        apiGroup.MapSessionEndpoints();

        apiGroup.MapSessionDivinationEndpoints();

        apiGroup.MapSanctumEndpoints();

        apiGroup.MapWardEndpoints();

        apiGroup.MapPromptEndpoints();

        apiGroup.MapApprenticeEndpoints();

        ArcanumSettings startupSettings = app.Services.GetRequiredService<IOptionsMonitor<ArcanumSettings>>().CurrentValue;

        apiGroup.MapA2AServer(startupSettings);

        apiGroup.MapCodexEndpoints();

        apiGroup.MapProviderTestEndpoints();

        apiGroup.MapProvingGroundsEndpoints();

        apiGroup.MapWorkspaceEndpoints();

        apiGroup.MapWorkspaceDivinationEndpoints();

        apiGroup.MapEmbeddingsResetEndpoints();

        apiGroup.MapEventEndpoints();

        apiGroup.MapDaemonEndpoints();
    }

}
