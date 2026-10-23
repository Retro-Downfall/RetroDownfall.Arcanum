using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed class SessionAttachmentDocumentExtractorsTests
{
    public static TheoryData<string> RegistryMimeTypes()
    {
        TheoryData<string> data = [];

        foreach (SessionAttachmentComprehensionFormat format in InProcessFormats())
        {
            data.Add(format.MimeType);
        }

        return data;
    }

    /// <summary>
    /// The registry's formats that run in the process. PDF is registered but is parsed only out of process, so the
    /// static in-process table holds no host for it; its isolated host arrives with the document worker.
    /// </summary>
    private static IEnumerable<SessionAttachmentComprehensionFormat> InProcessFormats() =>
        SessionAttachmentComprehensionPolicy.Formats.Where(
            static format => format.MimeType != SessionAttachmentComprehensionPolicy.PdfMimeType);

    [Theory]
    [MemberData(nameof(RegistryMimeTypes))]
    public void For_resolves_a_host_for_every_registry_mime(string mimeType)
    {
        Assert.NotNull(SessionAttachmentDocumentExtractors.For(mimeType));
    }

    [Theory]
    [MemberData(nameof(RegistryMimeTypes))]
    public void For_ignores_case_whitespace_and_parameters(string mimeType)
    {
        SessionAttachmentDocumentExtractionHost? expected = SessionAttachmentDocumentExtractors.For(mimeType);

        Assert.NotNull(expected);

        Assert.Same(
            expected,
            SessionAttachmentDocumentExtractors.For(" " + mimeType.ToUpperInvariant() + " ; charset=utf-8"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("text/plain")]
    [InlineData("application/rtf")]
    [InlineData("application/octet-stream")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.template")]
    [InlineData("application/vnd.oasis.opendocument.text-template")]
    public void For_resolves_nothing_outside_the_registry(string mimeType)
    {
        Assert.Null(SessionAttachmentDocumentExtractors.For(mimeType));
    }

    [Fact]
    public void For_resolves_the_container_to_the_default_host()
    {
        Assert.Same(
            SessionAttachmentDocumentExtractionHost.Default,
            SessionAttachmentDocumentExtractors.For(SessionAttachmentComprehensionPolicy.MimeType));
    }

    [Theory]
    [InlineData(
        SessionAttachmentComprehensionPolicy.WordDocumentMimeType,
        SessionAttachmentComprehensionPolicy.WordMacroEnabledDocumentMimeType)]
    [InlineData(
        SessionAttachmentComprehensionPolicy.ExcelWorkbookMimeType,
        SessionAttachmentComprehensionPolicy.ExcelMacroEnabledWorkbookMimeType)]
    [InlineData(
        SessionAttachmentComprehensionPolicy.PowerPointPresentationMimeType,
        SessionAttachmentComprehensionPolicy.PowerPointMacroEnabledPresentationMimeType)]
    [InlineData(
        SessionAttachmentComprehensionPolicy.VisioDrawingMimeType,
        SessionAttachmentComprehensionPolicy.VisioMacroEnabledDrawingMimeType)]
    public void For_shares_one_host_between_a_format_and_its_macro_enabled_variant(
        string baseMimeType,
        string macroEnabledMimeType)
    {
        SessionAttachmentDocumentExtractionHost? host = SessionAttachmentDocumentExtractors.For(baseMimeType);

        Assert.NotNull(host);

        Assert.Same(host, SessionAttachmentDocumentExtractors.For(macroEnabledMimeType));
    }

    [Fact]
    public void For_resolves_exactly_one_host_per_parser_family()
    {
        string[] families =
        [
            SessionAttachmentComprehensionPolicy.MimeType,
            SessionAttachmentComprehensionPolicy.WordDocumentMimeType,
            SessionAttachmentComprehensionPolicy.ExcelWorkbookMimeType,
            SessionAttachmentComprehensionPolicy.PowerPointPresentationMimeType,
            SessionAttachmentComprehensionPolicy.OpenDocumentTextMimeType,
            SessionAttachmentComprehensionPolicy.OpenDocumentSpreadsheetMimeType,
            SessionAttachmentComprehensionPolicy.OpenDocumentPresentationMimeType,
            SessionAttachmentComprehensionPolicy.VisioDrawingMimeType,
        ];

        SessionAttachmentDocumentExtractionHost[] hosts =
        [
            .. families.Select(static mimeType => SessionAttachmentDocumentExtractors.For(mimeType)!),
        ];

        Assert.Equal(8, hosts.Distinct(ReferenceEqualityComparer.Instance).Count());

        Assert.Equal(
            8,
            InProcessFormats()
                .Select(static format => SessionAttachmentDocumentExtractors.For(format.MimeType))
                .Distinct(ReferenceEqualityComparer.Instance)
                .Count());
    }

    [Theory]
    [MemberData(nameof(RegistryMimeTypes))]
    public async Task ExtractDocument_InjectedHostOverridesTheRegistryForEveryFormat(string mimeType)
    {
        DelegateExtractor extractor = new((_, output, _) =>
        {
            output.TryAppend("injected");

            return ValueTask.CompletedTask;
        });

        SessionAttachmentDocumentExtractionHost host = new(extractor.ExtractAsync, TimeSpan.FromSeconds(5));

        byte[] bytes = "payload"u8.ToArray();

        SessionAttachmentExtractionResult result = await SessionAttachmentTextExtractor.ExtractDocumentAsync(
            new NoOpSessionAttachmentStore(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes)),
            Record(mimeType, bytes.Length),
            Settings(),
            1024,
            CancellationToken.None,
            host);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("injected", result.Text);
    }

    private static ArcanumSettings Settings() => new()
    {
        Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = true },
    };

    private static SessionAttachmentRecord Record(string mimeType, long length) => new(
        Guid.NewGuid(), Guid.NewGuid(), null, null, SessionAttachmentState.Bound,
        "document", "document", 1, "untrusted/path", "hash", mimeType, length, SessionAttachmentKind.Binary,
        DateTimeOffset.UtcNow);

    private sealed class DelegateExtractor(
        Func<ReadOnlyMemory<byte>, ISessionAttachmentDocumentTextSink, CancellationToken, ValueTask> extract)
        : ISessionAttachmentDocumentExtractor
    {
        public ValueTask ExtractAsync(
            ReadOnlyMemory<byte> bytes,
            ISessionAttachmentDocumentTextSink output,
            CancellationToken cancellationToken) => extract(bytes, output, cancellationToken);
    }
}
