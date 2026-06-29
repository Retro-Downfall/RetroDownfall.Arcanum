using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using RetroDownfall.Arcanum.Api.Middleware;
using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Api.Configuration;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Api.Daemons;
using RetroDownfall.Arcanum.Api.Mcp;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Perception;
using RetroDownfall.Arcanum.Api.ProvingGrounds;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Spells;
using RetroDownfall.Arcanum.Api.Workspaces;
using RetroDownfall.Arcanum.Api.LlamaCpp;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Configuration;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;
using RetroDownfall.Arcanum.Infrastructure.Logging;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
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

        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, ArcanumJsonContext.Default));

        services.AddHttpClient(
            "OpenAiCompatibleProvider",
            static (sp, client) =>
            {
                _ = sp;

                client.Timeout = Timeout.InfiniteTimeSpan;
            });

        services.AddSingleton<IChatClientFactory, ChatClientFactory>();

        services.AddSingleton<InferenceTokenizerResolver>();

        services.AddSingleton<ManaPreflight>();

        services.AddSingleton<IManaMeter, TheForge.ManaMeter>();

        services.AddSingleton<PromptRenderer>();

        services.AddScoped<GrimoireTurnWriter>();

        services.AddScoped<InferenceContextBuilder>();

        services.AddScoped<ToolExecutionPipeline>();

        services.AddScoped<IArcanumIntelligenceProvider, WizardIntelligenceProvider>();

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

    public static void MapArcanumEndpoints(this WebApplication app)
    {
        bool rateLimitEnabled = IsRateLimitEnabled(app.Configuration);

        RouteGroupBuilder openAiV1 = app.MapGroup("/v1").AddEndpointFilter<ApiKeyEndpointFilter>();

        if (rateLimitEnabled)
        {
            openAiV1 = openAiV1.RequireRateLimiting(ArcanumRateLimiterPolicyName);
        }

        openAiV1.MapOpenAiV1ChatCompletions();

        openAiV1.MapOpenAiV1Models();

        var apiGroup = app.MapGroup("/api").AddEndpointFilter<ApiKeyEndpointFilter>();

        if (rateLimitEnabled)
        {
            apiGroup = apiGroup.RequireRateLimiting(ArcanumRateLimiterPolicyName);
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

        apiGroup.MapLlamaEndpoints();

        apiGroup.MapConfigurationEndpoints();

        apiGroup.MapIntelligenceEndpoints();

        apiGroup.MapMcpEndpoints();

        apiGroup.MapPerceptionEndpoints();

        apiGroup.MapLoreEndpoints();

        apiGroup.MapSpellEndpoints();

        apiGroup.MapSpellForgeEndpoints();

        apiGroup.MapSpellExecutionEndpoints();

        apiGroup.MapCampaignEndpoints();

        apiGroup.MapSessionEndpoints();

        apiGroup.MapSanctumEndpoints();

        apiGroup.MapWardEndpoints();

        apiGroup.MapPromptEndpoints();

        apiGroup.MapApprenticeEndpoints();

        apiGroup.MapCodexEndpoints();

        apiGroup.MapProviderTestEndpoints();

        apiGroup.MapProvingGroundsEndpoints();

        apiGroup.MapWorkspaceEndpoints();

        apiGroup.MapEventEndpoints();

        apiGroup.MapDaemonEndpoints();
    }

}
