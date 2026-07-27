using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.Tools;

/// <summary>
/// Default implementation of <see cref="IBuiltInToolRegistry"/>. Exposes tools that can be invoked
/// without a resolved spell or session context (for example, the <c>browse_web</c> diagnostic).
/// </summary>
public sealed class BuiltInToolRegistry : IBuiltInToolRegistry
{

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly IOptionsSnapshot<ArcanumSettings> _settings;

    private readonly ILogger<ArcanumBrowseWebTool>? _browseWebLogger;

    public BuiltInToolRegistry(
        IHttpClientFactory httpClientFactory,
        IOptionsSnapshot<ArcanumSettings> settings,
        ILogger<ArcanumBrowseWebTool>? browseWebLogger = null)
    {
        _httpClientFactory = httpClientFactory;

        _settings = settings;

        _browseWebLogger = browseWebLogger;
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
            names.Add(ArcanumBrowseWebTool.ToolName);
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
            return Result<JsonElement>.Failure(new Error(ErrorCodes.Hub.Error, $"Tool invocation failed: {ex.Message}"));
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
            return new ArcanumBrowseWebTool(_httpClientFactory, _settings, _browseWebLogger);
        }

        return null;
    }

}
