using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Core.Weave.Tapestry;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// One MIME normalisation, in C# and in every SQL statement that decides whether a stored MIME is a Comprehension
/// document: parameters are cut at the first semicolon, only space, tab, CR and LF are trimmed, and case is folded
/// as ASCII. A MIME one side reads as a document and the other does not lets document text into a corpus that
/// excludes it, or keeps it out of a retrieval that wants it.
/// </summary>
public static class SessionAttachmentMimeNormalizationData
{
    private const string Odt = "application/vnd.oasis.opendocument.text";

    /// <summary>The spellings under test and whether the shared normalisation reads each as the registry ODT type.</summary>
    public static TheoryData<string, bool> Spellings() => new()
    {
        { Odt, true },
        { "  Application/Vnd.OASIS.OpenDocument.Text ; charset=binary", true },
        { Odt + "\t", true },
        { Odt + "\r\n", true },
        { "\n" + Odt + "; q=1", true },
        { "\t" + Odt + "\t;charset=x", true },
        { Odt + " ", false },
        { " " + Odt, false },
        { Odt + "\f", false },
        { Odt + "\v", false },
        { Odt + "\u0085", false },
        { Odt + " ", false },
        { "applıcation/vnd.oasis.opendocument.text", false },
        { ";" + Odt, false },
    };
}

public sealed class SessionAttachmentMimeNormalizationTests
{
    public static TheoryData<string, bool> Spellings() => SessionAttachmentMimeNormalizationData.Spellings();

    [Theory]
    [MemberData(nameof(Spellings))]
    public void TryGetFormat_ReadsOnlyTheSharedNormalisationAsARegistryType(string mimeType, bool isDocument)
    {
        Assert.Equal(isDocument, SessionAttachmentComprehensionPolicy.TryGetFormat(mimeType, out _));
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public void IsEligibleDocument_FollowsTheSharedNormalisation(string mimeType, bool isDocument)
    {
        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(Guid.NewGuid(), mimeType, 1);

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = true },
        };

        Assert.Equal(isDocument, SessionAttachmentComprehensionPolicy.IsEligibleDocument(record, settings));
    }

    [Theory]
    [InlineData("application/vnd.oasis.opendocument.text", "application/vnd.oasis.opendocument.text")]
    [InlineData("  APPLICATION/VND.OASIS.OPENDOCUMENT.TEXT\t;charset=binary", "application/vnd.oasis.opendocument.text")]
    [InlineData("\r\n\ttext/PLAIN \r\n", "text/plain")]
    [InlineData(";text/plain", "")]
    [InlineData("", "")]
    [InlineData(" text/plain", " text/plain")]
    [InlineData("text/plain\f", "text/plain\f")]
    [InlineData("İı", "İı")]
    public void NormalizeMimeType_StripsParametersTrimsAsciiWhitespaceAndFoldsAsciiCase(string mimeType, string expected)
    {
        Assert.Equal(expected, SessionAttachmentComprehensionPolicy.NormalizeMimeType(mimeType));
    }
}

/// <summary>
/// Every SQL site that classifies a stored MIME, driven with the same spellings as the C# policy. Each site must treat a
/// spelling as a document exactly when <see cref="SessionAttachmentComprehensionPolicy.TryGetFormat"/> does.
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class SessionAttachmentMimeNormalizationSqlTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private const int Dimensions = 64;

    private string _dbPath = string.Empty;

    private string _root = string.Empty;

    private ArcanumDbContext? _db;

    private ArcanumSettings _settings = null!;

    private SessionAttachmentStore? _attachments;

    private SessionAttachmentIndexRepository? _index;

    public static TheoryData<string, bool> Spellings() => SessionAttachmentMimeNormalizationData.Spellings();

    public Task InitializeAsync()
    {
        _dbPath = fixture.CopyDatabase();

        _root = Path.Combine(Path.GetTempPath(), "arcanum-mime-normalization-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_root);

        _db = fixture.CreateContext(_dbPath);

        _settings = new ArcanumSettings
        {
            Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings { Dimensions = Dimensions },
            },
        };

        _attachments = new SessionAttachmentStore(_db, Options.Create(_settings), _root, TestEncryptedBlobStore.Create());

        _index = new SessionAttachmentIndexRepository(_db, new WeaveIndexAvailability(), new TestOptionsMonitor<ArcanumSettings>(_settings));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Spellings))]
    public async Task TapestryCorpus_ExcludesExactlyTheChunksCSharpReadsAsDocuments(string mimeType, bool isDocument)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Assert.Equal(isDocument, SessionAttachmentComprehensionPolicy.TryGetFormat(mimeType, out _));

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistBinaryAsync(sessionId, "subject", mimeType);

        await SeedPublishedChunksAsync(attachment, 8, UnitVector(0));

        TapestryStore tapestry = new(_db!, new WeaveIndexAvailability());

        TapestryCorpusIdentity identity = await tapestry.GetCorpusIdentityAsync(
            TapestryScope.ForSessionAttachment(sessionId), 100, CancellationToken.None);

        Assert.Equal(isDocument ? 0 : 8, identity.LeafCount);
    }

    [SkippableTheory]
    [MemberData(nameof(Spellings))]
    public async Task RetrievalWithComprehensionOff_PreFiltersExactlyTheChunksCSharpReadsAsDocuments(string mimeType, bool isDocument)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Assert.Equal(isDocument, SessionAttachmentComprehensionPolicy.TryGetFormat(mimeType, out _));

        Guid sessionId = Guid.NewGuid();

        // More chunks than the retrieval ceiling, each an exact match for the query, so any that the SQL
        // pre-filter fails to exclude take every slot before the ordinary text can be scored.
        SessionAttachmentRecord subject = await PersistBinaryAsync(sessionId, "subject", mimeType);

        await SeedPublishedChunksAsync(subject, 8, UnitVector(0));

        SessionAttachmentRecord ordinary = await _attachments!.PersistNewAsync(
            sessionId, null, null, "ordinary", "ordinary.txt", "ordinary"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

        await SeedPublishedChunksAsync(ordinary, 1, TiltedVector(), "ordinary text survives");

        _settings.Features.Comprehension = false;

        SessionAttachmentRetrievedChunk[] hits = await CreateRetrievalService().SearchAsync(
            sessionId, new Embedding<float>(UnitVector(0)), false, CancellationToken.None);

        if (isDocument)
        {
            Assert.Equal("ordinary text survives", Assert.Single(hits).Content);
        }
        else
        {
            Assert.Equal(5, hits.Length);

            Assert.All(hits, static hit => Assert.StartsWith("subject sentinel", hit.Content, StringComparison.Ordinal));
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Spellings))]
    public async Task Reconciliation_RequeuesExactlyTheAttachmentsCSharpReadsAsDocuments(string mimeType, bool isDocument)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Assert.Equal(isDocument, SessionAttachmentComprehensionPolicy.TryGetFormat(mimeType, out _));

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord subject = await PersistBinaryAsync(sessionId, "subject", mimeType);

        // The state indexing leaves behind when Comprehension was off: not eligible, nothing extracted.
        await _index!.SetPendingAsync(subject, 1, CancellationToken.None);

        await _index.MarkWithoutIndexAsync(
            subject.Id, subject.ContentSha256, SessionAttachmentIndexStatus.NotEligible, 1, null, null, CancellationToken.None);

        _settings.Features.Comprehension = false;

        Assert.Empty(await _index.ReconcileAndFindPendingAsync(Dimensions, 10, CancellationToken.None));

        _settings.Features.Comprehension = true;

        SessionAttachmentIndexRequest[] requeued = await _index.ReconcileAndFindPendingAsync(Dimensions, 10, CancellationToken.None);

        Assert.Equal(isDocument, requeued.Any(request => request.AttachmentId == subject.Id));
    }

    [SkippableFact]
    public async Task SqlNormalisation_IsByteForByteTheCSharpNormalisation()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string[] battery =
        [
            .. SessionAttachmentMimeNormalizationData.Spellings().Select(static row => (string)row[0]),
            "",
            ";",
            " ",
            " \t\r\n ",
            "text/plain",
            "TEXT/Plain; Charset=UTF-8",
            "\t\r\nText/Plain\t\r\n;x=y;z",
            "text/plain;;",
            "text/pl\tain",
            "\u212Aelvin/\u0130\u0131",
            "\u00C4PPLICATION/\u00E4",
            "application/vnd.ms-excel.sheet.macroEnabled.12 ; q=0.5",
            "\u00A0\u2003text/plain\u2003\u00A0",
            "text/plain\f\v\u0085\u2028",
        ];

        System.Data.Common.DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        foreach (string mimeType in battery)
        {
            await using System.Data.Common.DbCommand command = connection.CreateCommand();

            command.CommandText = "SELECT " + SessionAttachmentDocumentMimeSql.NormalizedExpression("@mime");

            System.Data.Common.DbParameter parameter = command.CreateParameter();

            parameter.ParameterName = "@mime";

            parameter.Value = mimeType;

            command.Parameters.Add(parameter);

            Assert.Equal(
                SessionAttachmentComprehensionPolicy.NormalizeMimeType(mimeType),
                (string?)await command.ExecuteScalarAsync());
        }
    }

    private Task<SessionAttachmentRecord> PersistBinaryAsync(Guid sessionId, string logicalKey, string mimeType) =>
        _attachments!.PersistNewAsync(
            sessionId, null, null, logicalKey, logicalKey + ".bin", "PK\u0003\u0004package"u8.ToArray(), mimeType, SessionAttachmentKind.Binary);

    private SessionAttachmentRetrievalService CreateRetrievalService() =>
        new(
            new TestOptionsMonitor<ArcanumSettings>(_settings),
            new DivinationService(
                _db!,
                new WeaveIndexAvailability(),
                NullLogger<DivinationService>.Instance,
                new TestOptionsMonitor<ArcanumSettings>(_settings)),
            _index!,
            NullLogger<SessionAttachmentRetrievalService>.Instance);

    /// <summary>Publishes chunks for an attachment without extraction; each chunk carries the attachment's MIME, as indexing does.</summary>
    private async Task SeedPublishedChunksAsync(
        SessionAttachmentRecord attachment,
        int count,
        float[] vector,
        string? fixedContent = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _index!.SetPendingAsync(attachment, 1, CancellationToken.None);

        SessionAttachmentIndexCheckpoint checkpoint = await _index.BeginReplaceAsync(
            attachment, Dimensions, "test-pipeline", now, CancellationToken.None);

        SessionAttachmentTextChunk[] chunks =
        [
            .. Enumerable.Range(0, count).Select(index => new SessionAttachmentTextChunk(
                index, index * 20, (index * 20) + 17, index + 1, index + 1, fixedContent ?? ("subject sentinel " + index))),
        ];

        await _index.AppendReplaceBatchAsync(
            attachment,
            checkpoint.GenerationId,
            chunks,
            [.. chunks.Select(_ => new Embedding<float>(vector))],
            Dimensions,
            now,
            now,
            CancellationToken.None);

        await _index.CompleteReplaceAsync(
            attachment, checkpoint.GenerationId, count, now, now, 1, CancellationToken.None);
    }

    private static float[] UnitVector(int axis)
    {
        float[] vector = new float[Dimensions];

        vector[axis] = 1f;

        return vector;
    }

    private static float[] TiltedVector()
    {
        float[] vector = new float[Dimensions];

        vector[0] = 0.8f;

        vector[1] = 0.6f;

        return vector;
    }
}
