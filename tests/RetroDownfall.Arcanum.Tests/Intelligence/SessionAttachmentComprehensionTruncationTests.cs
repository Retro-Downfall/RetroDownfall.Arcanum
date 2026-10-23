using System.Text;

using Microsoft.Extensions.AI;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// A document the extractor could only partly retain is never framed as a complete one. Every model-facing path
/// (attach, refresh, per-turn reference and durable pin) names the truncation in its untrusted-data notice.
/// </summary>
public sealed class SessionAttachmentComprehensionTruncationTests
{
    private const string Truncated = "; truncated at the extraction limit";

    private const string ContainerLabel = "Comprehension text container";

    private const string WorkbookLabel = "Excel workbook";

    public static TheoryData<string> TruncatedDocuments() =>
    [
        "container-over-the-character-cap",
        "workbook-past-shared-string-retention",
    ];

    [Theory]
    [MemberData(nameof(TruncatedDocuments))]
    public async Task Attach_TruncatedDocument_SaysSoInItsNotice(string scenario)
    {
        SessionAttachmentTurnBudget.BeginTurn();

        (Guid sessionId, SessionAttachmentRecord record, byte[] bytes, string label) = await TruncatedDocumentAsync(scenario);

        IReadOnlyList<AIContent>? contents = await SessionAttachmentToolInjection.TryBuildContentsAsync(
            StoreFor(record, bytes), sessionId, record.LogicalKey, null, BothGates(), null);

        string text = Assert.IsType<TextContent>(Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<AIContent>>(contents))).Text;

        Assert.Contains($"UNTRUSTED DATA; {label}; layout not preserved{Truncated}", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(TruncatedDocuments))]
    public async Task Refresh_TruncatedDocument_SaysSoInItsNotice(string scenario)
    {
        SessionAttachmentTurnBudget.BeginTurn();

        (_, SessionAttachmentRecord record, byte[] bytes, string label) = await TruncatedDocumentAsync(scenario);

        IReadOnlyList<AIContent>? contents = await SessionAttachmentToolInjection.TryBuildRefreshedContentsAsync(
            StoreFor(record, bytes), record, BothGates(), null);

        string text = Assert.IsType<TextContent>(Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<AIContent>>(contents))).Text;

        Assert.Contains($"UNTRUSTED DATA; {label}; layout not preserved{Truncated}", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(TruncatedDocuments))]
    public async Task PerTurnReference_TruncatedDocument_SaysSoInItsNotice(string scenario)
    {
        (_, SessionAttachmentRecord record, byte[] bytes, string label) = await TruncatedDocumentAsync(scenario);

        SessionAttachmentReferenceMaterialization materialized = await SessionAttachmentTurnService.MaterializeReferenceAsync(
            record, StoreFor(record, bytes), BothGates(), "model", CancellationToken.None);

        Assert.Null(materialized.ErrorMessage);

        string text = Assert.IsType<TextContent>(Assert.Single(materialized.Contents)).Text;

        Assert.Contains($"UNTRUSTED DATA; {label}; layout not preserved{Truncated}", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(TruncatedDocuments))]
    public async Task Pin_TruncatedDocument_IsReportedTruncatedWithItsNotice(string scenario)
    {
        (Guid sessionId, SessionAttachmentRecord record, byte[] bytes, string label) = await TruncatedDocumentAsync(scenario);

        string text = await PinTextAsync(record, bytes, sessionId, BothGates());

        Assert.Contains("status: Truncated", text, StringComparison.Ordinal);

        Assert.DoesNotContain("status: Current", text, StringComparison.Ordinal);

        Assert.Contains($"UNTRUSTED DATA; {label}; layout not preserved{Truncated}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteDocument_KeepsTheExistingNoticeOnEveryPath()
    {
        SessionAttachmentTurnBudget.BeginTurn();

        Guid sessionId = Guid.NewGuid();

        byte[] bytes = Encoding.UTF8.GetBytes("ARCCOMP1\nA complete document sentinel");

        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(
            sessionId, SessionAttachmentComprehensionPolicy.MimeType, bytes.Length);

        NoOpSessionAttachmentStore store = StoreFor(record, bytes);

        IReadOnlyList<AIContent>? attached = await SessionAttachmentToolInjection.TryBuildContentsAsync(
            store, sessionId, record.LogicalKey, null, BothGates(), null);

        SessionAttachmentTurnBudget.BeginTurn();

        IReadOnlyList<AIContent>? refreshed = await SessionAttachmentToolInjection.TryBuildRefreshedContentsAsync(
            store, record, BothGates(), null);

        SessionAttachmentReferenceMaterialization reference = await SessionAttachmentTurnService.MaterializeReferenceAsync(
            record, store, BothGates(), "model", CancellationToken.None);

        string pinned = await PinTextAsync(record, bytes, sessionId, BothGates());

        string[] texts =
        [
            Assert.IsType<TextContent>(Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<AIContent>>(attached))).Text,
            Assert.IsType<TextContent>(Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<AIContent>>(refreshed))).Text,
            Assert.IsType<TextContent>(Assert.Single(reference.Contents)).Text,
            pinned,
        ];

        foreach (string text in texts)
        {
            Assert.Contains($"UNTRUSTED DATA; {ContainerLabel}; layout not preserved", text, StringComparison.Ordinal);

            Assert.DoesNotContain("truncated at the extraction limit", text, StringComparison.Ordinal);

            Assert.Contains("A complete document sentinel", text, StringComparison.Ordinal);
        }

        Assert.Contains("status: Current", pinned, StringComparison.Ordinal);
    }

    private static async Task<string> PinTextAsync(
        SessionAttachmentRecord record,
        byte[] bytes,
        Guid sessionId,
        ArcanumSettings settings)
    {
        SessionContextPinRecord pin = new(
            Guid.NewGuid(), sessionId, SessionContextPinKind.Attachment,
            record.Id.ToString("D"), "document", record.ContentSha256, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        // An attachment pin never reads a session entry, so the session repository is not consulted.
        SessionContextPinMaterializer materializer = new(
            new StaticPinStore(pin),
            StoreFor(record, bytes),
            null!,
            new TestOptionsSnapshot<ArcanumSettings>(settings));

        SessionContextPinMaterialization result = await materializer.MaterializeAsync(
            sessionId, Path.GetTempPath(), CancellationToken.None);

        return Assert.IsType<TextContent>(Assert.Single(result.Contents)).Text;
    }

    private static NoOpSessionAttachmentStore StoreFor(SessionAttachmentRecord record, byte[] bytes) =>
        new(
            record,
            readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes),
            openRead: (_, _) => throw new InvalidOperationException("Documents must use stored bytes."));

    private static ArcanumSettings BothGates() =>
        new()
        {
            Features = new FeatureSettings { Attachments = true, AttachmentRetrieval = true, Comprehension = true },
        };

    /// <summary>A document whose extraction reports truncation, proved against the extractor before a path is judged.</summary>
    private static async Task<(Guid SessionId, SessionAttachmentRecord Record, byte[] Bytes, string Label)> TruncatedDocumentAsync(
        string scenario)
    {
        Guid sessionId = Guid.NewGuid();

        (string mimeType, byte[] bytes, string label) = scenario switch
        {
            "container-over-the-character-cap" => (
                SessionAttachmentComprehensionPolicy.MimeType,
                Encoding.UTF8.GetBytes("ARCCOMP1\n" + new string('a', 300_000)),
                ContainerLabel),
            _ => (
                SessionAttachmentComprehensionPolicy.ExcelWorkbookMimeType,
                RetentionOverflowWorkbook(),
                WorkbookLabel),
        };

        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, mimeType, bytes.Length);

        SessionAttachmentExtractionResult extraction = await SessionAttachmentTextExtractor.ExtractDocumentAsync(
            StoreFor(record, bytes), record, BothGates(), 1024 * 1024, CancellationToken.None);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, extraction.Status);

        Assert.True(extraction.WasTruncated, "The fixture must be one the extractor reports as truncated.");

        return (sessionId, record, bytes, label);
    }

    /// <summary>
    /// Two shared strings, the second larger than the shared-string retention. Extraction stops there, so a third row of
    /// inline text after them is lost: a partial workbook that reads as the whole one unless the notice says otherwise.
    /// </summary>
    private static byte[] RetentionOverflowWorkbook()
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        const string packageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

        Random random = new(7);

        StringBuilder filler = new(1_048_600);

        for (int index = 0; index < 1_048_600; index++)
        {
            filler.Append((char)('a' + random.Next(0, 26)));
        }

        return ComprehensionPackageBuilder.Build(
            ("_rels/.rels", $"<Relationships xmlns=\"{packageRel}\"><Relationship Id=\"rId1\" Type=\"{rel}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", $"<workbook xmlns=\"{main}\" xmlns:r=\"{rel}\"><sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rIdS1\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"{packageRel}\"><Relationship Id=\"rIdS1\" Type=\"{rel}/worksheet\" Target=\"/xl/worksheets/sheet1.xml\"/><Relationship Id=\"rIdSst\" Type=\"{rel}/sharedStrings\" Target=\"/xl/sharedStrings.xml\"/></Relationships>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{main}\" xmlns:r=\"{rel}\"><sheetData><row><c r=\"A1\" t=\"s\"><v>0</v></c></row><row><c r=\"A2\" t=\"s\"><v>1</v></c></row><row><c r=\"A3\" t=\"inlineStr\"><is><t>THIRD ROW VISIBLE TEXT</t></is></c></row></sheetData></worksheet>"),
            ("xl/sharedStrings.xml", $"<sst xmlns=\"{main}\"><si><t>visible first row</t></si><si><t>{filler}</t></si></sst>"));
    }

    private sealed class StaticPinStore(params SessionContextPinRecord[] pins) : ISessionContextPinStore
    {
        public Task<IReadOnlyList<SessionContextPinRecord>> ListAsync(
            Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionContextPinRecord>>(pins);

        public Task<SessionContextPinRecord> UpsertAsync(
            Guid sessionId, SessionContextPinKind kind, string targetIdentifier, string displayLabel,
            string? contentVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            Guid sessionId, Guid pinId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
