using System.Globalization;
using System.Text.RegularExpressions;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>Only the explicit PDF harness mode enables these production dependencies.</summary>
internal sealed record PdfQualificationOptions(string Model, int Window, int Dimensions)
{
    public const string ModeVariable = "ARCANUM_OLLAMA_QUALIFICATION_PDF";

    public const string ModelVariable = "ARCANUM_OLLAMA_QUALIFICATION_PDF_EMBEDDING_MODEL";

    public const string WindowVariable = "ARCANUM_OLLAMA_QUALIFICATION_PDF_EMBEDDING_WINDOW";

    public const string DimensionsVariable = "ARCANUM_OLLAMA_QUALIFICATION_PDF_EMBEDDING_DIMENSIONS";

    public static PdfQualificationOptions? Read(Func<string, string?> environment)
    {
        string? mode = environment(ModeVariable);

        if (mode is null or "0") return null;

        if (mode != "1") throw new ArgumentException("PDF qualification requires its exact mode opt-in.");

        string model = environment(ModelVariable) ?? LongRestQualificationOptions.DefaultEmbeddingModel;

        int window = ReadInteger(environment(WindowVariable), LongRestQualificationOptions.DefaultEmbeddingWindow);

        int dimensions = ReadInteger(environment(DimensionsVariable), LongRestQualificationOptions.DefaultEmbeddingDimensions);

        if (!Regex.IsMatch(model, "^[A-Za-z0-9][A-Za-z0-9._:/-]*$", RegexOptions.CultureInvariant)
            || window is < 1024 or > 9_999_999 || dimensions is < 64 or > 4096)
        {
            throw new ArgumentException("Invalid PDF embedding model/window/dimensions.");
        }

        return new(model, window, dimensions);
    }

    private static int ReadInteger(string? raw, int fallback) => raw is null ? fallback
        : int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value
        : throw new ArgumentException("PDF embedding dimensions and window must be positive decimal integers.");
}

internal sealed record PdfQualificationDependencies(PdfQualificationOptions Options, Uri Endpoint)
{
    public const string ProviderName = "local-pdf-embeddings";

    public const string CredentialVariable = "ARCANUM_OLLAMA_QUALIFICATION_PDF_EMBEDDING_KEY";

    // Reuse the existing recording forwarder's closed credential classification, never operator credentials.
    public const string CredentialPlaceholder = LongRestQualificationConfiguration.EmbeddingCredentialPlaceholder;

    public void Configure(ArcanumSettings settings)
    {
        settings.Features.AttachmentRetrieval = true;

        settings.Features.Comprehension = true;

        settings.Security.AllowedUploadMimeTypes = [.. settings.Security.AllowedUploadMimeTypes, "application/pdf"];

        settings.Providers = [.. settings.Providers, new ProviderSettings
        {
            Name = ProviderName,
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = Endpoint.AbsoluteUri,
            CredentialEnvironmentVariable = CredentialVariable,
            ContextWindowLimit = Options.Window,
            Models = [new ModelEntry(Options.Model, SupportsVision: false, SupportsTools: false)],
        }];

        settings.Integrations.Embeddings = new EmbeddingIntegrationSettings
        {
            Provider = ProviderName,
            Model = Options.Model,
            Dimensions = Options.Dimensions,
        };
    }
}
