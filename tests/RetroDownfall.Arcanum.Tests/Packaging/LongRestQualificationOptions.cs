using System.Text.RegularExpressions;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>
/// The operator's choices for the Long Rest qualification, read from the environment the wrapper sets and
/// validated before anything is launched. No value here is a credential: the provider keys the isolated
/// installation uses are synthetic placeholders the test owns.
/// </summary>
internal sealed record LongRestQualificationOptions(
    string Executable,
    string ChatModel,
    string DefaultModel,
    string EmbeddingModel,
    int EmbeddingWindow,
    int EmbeddingDimensions,
    Uri Endpoint,
    string? ResultsDirectory,
    string? ReceiptPath)
{
    public const string OptInVariable = "ARCANUM_RUN_LOCAL_OLLAMA_LONG_REST_QUALIFICATION";

    public const string ExecutableVariable = "ARCANUM_PUBLISHED_EXECUTABLE";

    public const string ChatModelVariable = "ARCANUM_OLLAMA_LONG_REST_CHAT_MODEL";

    public const string DefaultModelVariable = "ARCANUM_OLLAMA_LONG_REST_DEFAULT_MODEL";

    public const string EmbeddingModelVariable = "ARCANUM_OLLAMA_LONG_REST_EMBEDDING_MODEL";

    public const string EmbeddingWindowVariable = "ARCANUM_OLLAMA_LONG_REST_EMBEDDING_WINDOW";

    public const string EmbeddingDimensionsVariable = "ARCANUM_OLLAMA_LONG_REST_EMBEDDING_DIMENSIONS";

    public const string EndpointVariable = "ARCANUM_OLLAMA_LONG_REST_ENDPOINT";

    public const string ResultsVariable = "ARCANUM_OLLAMA_LONG_REST_RESULTS";

    public const string ReceiptVariable = "ARCANUM_OLLAMA_LONG_REST_RECEIPT";

    public const string SuccessReceipt = "local-ollama-long-rest-qualification:v1";

    public const string DefaultChatModel = "gemma4:e4b";

    public const string DefaultDefaultModel = "ornith1.5:35b";

    public const string DefaultEmbeddingModel = "embeddinggemma";

    public const int DefaultEmbeddingWindow = 2048;

    /// <summary>The vector size of the default embedding model; Compendium allows 64 to 4,096.</summary>
    public const int DefaultEmbeddingDimensions = 768;

    public const int MinimumEmbeddingDimensions = 64;

    public const int MaximumEmbeddingDimensions = 4096;

    public const string DefaultEndpoint = "http://127.0.0.1:11434/v1/";

    private static readonly Regex ModelTag = new(
        "^[A-Za-z0-9][A-Za-z0-9._:/-]*$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    /// <summary>
    /// Reads and validates the options. Every defect is reported as the variable that carries it, never as a
    /// value, so a mistyped path or tag cannot echo anything sensitive into a test log.
    /// </summary>
    public static LongRestQualificationOptions Read(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        string executable = RequireValue(environment, ExecutableVariable);

        string chatModel = ReadModel(environment, ChatModelVariable, DefaultChatModel);

        string defaultModel = ReadModel(environment, DefaultModelVariable, DefaultDefaultModel);

        string embeddingModel = ReadModel(environment, EmbeddingModelVariable, DefaultEmbeddingModel);

        int embeddingWindow = DefaultEmbeddingWindow;

        string? rawWindow = environment(EmbeddingWindowVariable)?.Trim();

        if (!string.IsNullOrEmpty(rawWindow)
            && (!int.TryParse(rawWindow, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out embeddingWindow)
                || embeddingWindow < 1))
        {
            throw new InvalidOperationException($"{EmbeddingWindowVariable} must be a positive token count.");
        }

        int embeddingDimensions = DefaultEmbeddingDimensions;

        string? rawDimensions = environment(EmbeddingDimensionsVariable)?.Trim();

        if (!string.IsNullOrEmpty(rawDimensions)
            && (!int.TryParse(rawDimensions, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out embeddingDimensions)
                || embeddingDimensions is < MinimumEmbeddingDimensions or > MaximumEmbeddingDimensions))
        {
            throw new InvalidOperationException(
                $"{EmbeddingDimensionsVariable} must be an integer between {MinimumEmbeddingDimensions} and {MaximumEmbeddingDimensions}.");
        }

        if (!LocalOllamaQualificationGuards.IsLocalOllamaEndpoint(
                environment(EndpointVariable) ?? DefaultEndpoint,
                out Uri? endpoint))
        {
            throw new InvalidOperationException(
                $"{EndpointVariable} must be an HTTP /v1 URI on a literal loopback address without credentials, query, or fragment.");
        }

        string? results = environment(ResultsVariable);

        string? receipt = environment(ReceiptVariable);

        return new LongRestQualificationOptions(
            Path.GetFullPath(executable),
            chatModel,
            defaultModel,
            embeddingModel,
            embeddingWindow,
            embeddingDimensions,
            endpoint!,
            string.IsNullOrWhiteSpace(results) ? null : Path.GetFullPath(results),
            string.IsNullOrWhiteSpace(receipt) ? null : receipt);
    }

    private static string RequireValue(Func<string, string?> environment, string variable)
    {
        string? value = environment(variable);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{variable} is required after local qualification is enabled.");
        }

        return value;
    }

    private static string ReadModel(Func<string, string?> environment, string variable, string fallback)
    {
        string model = environment(variable)?.Trim() ?? fallback;

        if (!ModelTag.IsMatch(model))
        {
            throw new InvalidOperationException($"{variable} must be a nonblank Ollama model tag without whitespace or control characters.");
        }

        return model;
    }
}
