using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;

namespace RetroDownfall.Arcanum.Api.Intelligence.Tools;

/// <summary>
/// Default implementation of <see cref="IBuiltInToolRegistry"/>. Exposes tools that can be invoked
/// without a resolved spell or session context (for example, the <c>browse_web</c> diagnostic).
/// </summary>
public sealed class BuiltInToolRegistry : IBuiltInToolRegistry
{

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly IOptionsSnapshot<ArcanumSettings> _settings;

    private readonly IWebResearchProviderCatalog _webResearchProviders;

    private readonly ILogger<ArcanumBrowseWebTool>? _browseWebLogger;

    private readonly ILogger<ArcanumWebSearchTool>? _webSearchLogger;

    private readonly ILogger<ArcanumReadUrlTool>? _readUrlLogger;

    private readonly ILogger<BuiltInToolRegistry>? _logger;

    public BuiltInToolRegistry(
        IHttpClientFactory httpClientFactory,
        IOptionsSnapshot<ArcanumSettings> settings,
        ILogger<ArcanumBrowseWebTool>? browseWebLogger = null)
        : this(
            httpClientFactory,
            settings,
            CreateCompatibilityCatalog(httpClientFactory, settings),
            browseWebLogger,
            webSearchLogger: null,
            readUrlLogger: null,
            logger: null)
    {
    }

    public BuiltInToolRegistry(
        IHttpClientFactory httpClientFactory,
        IOptionsSnapshot<ArcanumSettings> settings,
        ILogger<ArcanumBrowseWebTool>? browseWebLogger,
        ILogger<BuiltInToolRegistry>? logger)
        : this(
            httpClientFactory,
            settings,
            CreateCompatibilityCatalog(httpClientFactory, settings),
            browseWebLogger,
            webSearchLogger: null,
            readUrlLogger: null,
            logger)
    {
    }

    public BuiltInToolRegistry(
        IHttpClientFactory httpClientFactory,
        IOptionsSnapshot<ArcanumSettings> settings,
        IWebResearchProviderCatalog webResearchProviders,
        ILogger<ArcanumBrowseWebTool>? browseWebLogger,
        ILogger<ArcanumWebSearchTool>? webSearchLogger,
        ILogger<ArcanumReadUrlTool>? readUrlLogger,
        ILogger<BuiltInToolRegistry>? logger)
    {
        _httpClientFactory = httpClientFactory;

        _settings = settings;

        _webResearchProviders = webResearchProviders;

        _browseWebLogger = browseWebLogger;

        _webSearchLogger = webSearchLogger;

        _readUrlLogger = readUrlLogger;

        _logger = logger;
    }

    public IReadOnlyList<string> GetToolNames()
    {
        List<string> names =
        [
            ArcanumLocalTimeTool.ToolName,
            ArcanumSystemInfoTool.ToolName,
        ];

        if (_settings.Value.ResolveWebBrowsing().Enabled)
        {
            names.Add(ArcanumWebSearchTool.ToolName);
            names.Add(ArcanumReadUrlTool.ToolName);
        }

        return names;
    }

    public async Task<Result<JsonElement>> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
    {
        AIFunction? tool = Resolve(toolName);

        if (tool is null)
        {
            return Result<JsonElement>.Failure(new Error(ErrorCodes.Mcp.ServerNotFound, $"Built-in tool '{toolName}' is not available."));
        }

        try
        {
            AIFunctionArguments args = arguments.ValueKind == JsonValueKind.Object
                ? new AIFunctionArguments(ToArgumentDictionary(arguments))
                : [];

            object? output = await tool
                .InvokeAsync(args, cancellationToken)
                .ConfigureAwait(false);

            string text = output switch
            {
                null => string.Empty,
                string s => s,
                JsonElement je => je.GetRawText(),
                _ => output.ToString() ?? string.Empty,
            };

            JsonElement element;

            try
            {
                using JsonDocument document = JsonDocument.Parse(text);

                element = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                element = JsonSerializer.SerializeToElement(text, ArcanumJsonContext.Default.String);
            }

            return Result<JsonElement>.Success(element);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                "Built-in tool {ToolName} invocation failed; exception type {ExceptionType}.",
                toolName,
                ex.GetType().FullName);

            return Result<JsonElement>.Failure(new Error(
                ErrorCodes.Hub.Error,
                ToolExecutionPipeline.PublicToolFailureMessage(toolName)));
        }
    }

    private static Dictionary<string, object?> ToArgumentDictionary(JsonElement arguments)
    {
        Dictionary<string, object?> dict = new(StringComparer.Ordinal);

        foreach (JsonProperty property in arguments.EnumerateObject())
        {
            dict[property.Name] = property.Value;
        }

        return dict;
    }

    private AIFunction? Resolve(string toolName)
    {
        if (string.Equals(toolName, ArcanumLocalTimeTool.ToolName, StringComparison.Ordinal))
        {
            return new ArcanumLocalTimeTool();
        }

        if (string.Equals(toolName, ArcanumSystemInfoTool.ToolName, StringComparison.Ordinal))
        {
            return new ArcanumSystemInfoTool();
        }

        if (string.Equals(toolName, ArcanumBrowseWebTool.ToolName, StringComparison.Ordinal)
            && _settings.Value.ResolveWebBrowsing().Enabled)
        {
            return new ArcanumBrowseWebTool(
                _httpClientFactory,
                _settings,
                _browseWebLogger);
        }

        if (string.Equals(toolName, ArcanumWebSearchTool.ToolName, StringComparison.Ordinal)
            && _settings.Value.ResolveWebBrowsing().Enabled)
        {
            return new ArcanumWebSearchTool(
                _webResearchProviders,
                _settings,
                _webSearchLogger);
        }

        if (string.Equals(toolName, ArcanumReadUrlTool.ToolName, StringComparison.Ordinal)
            && _settings.Value.ResolveWebBrowsing().Enabled)
        {
            return new ArcanumReadUrlTool(
                _webResearchProviders,
                _settings,
                _readUrlLogger);
        }

        return null;
    }

    private static IWebResearchProviderCatalog CreateCompatibilityCatalog(
        IHttpClientFactory httpClientFactory,
        IOptionsSnapshot<ArcanumSettings> settings) =>
        new WebResearchProviderCatalog(
            [
                new PerplexityWebProvider(
                    httpClientFactory,
                    new EnvironmentOnlyWebResearchApiKeyResolver(settings)),
                new LocalHttpWebProvider(
                    httpClientFactory,
                    new WebPageContentExtractor()),
            ]);

    private sealed class EnvironmentOnlyWebResearchApiKeyResolver(
        IOptionsSnapshot<ArcanumSettings> settings) : IWebResearchApiKeyResolver
    {
        public ValueTask<string?> ResolveApiKeyAsync(
            string providerName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? apiKey = string.Equals(
                    providerName,
                    WebResearchProviderNames.Perplexity,
                    StringComparison.OrdinalIgnoreCase)
                ? EnvironmentCredentialResolver.ResolveWebResearchApiKey(
                    settings.Value.ResolveWebBrowsing())
                : null;

            return ValueTask.FromResult(apiKey);
        }
    }

}
