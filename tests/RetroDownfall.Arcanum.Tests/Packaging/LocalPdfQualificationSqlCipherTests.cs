using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Packaging;

[Collection("Grimoire")]
public sealed class LocalPdfQualificationSqlCipherTests(GrimoireFixture fixture)
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_only_pdf_evidence_query_binds_the_actual_chunk_writer_and_canonical_attachment_identity(bool sameCase)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string path = fixture.CopyDatabase();

        try
        {
            await using var context = fixture.CreateContext(path);

            SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();

            Guid session = Guid.Parse("abcdef12-3456-4789-abcd-ef1234567890");

            Guid attachment = Guid.Parse("abcdef12-3456-4789-abcd-ef1234567891");

            PdfQualificationChallenge challenge = new("a123456789abcdef0123456789abcdef", "b123456789abcdef0123456789abcdef", 71, 8, 83, 11);

            await using (SqliteCommand seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO Sessions(Id,CreatedAt,UpdatedAt) VALUES(@session,@instant,@instant);
                    INSERT INTO SessionAttachments(Id,SessionId,State,LogicalKey,OriginalFileName,Version,RelativePath,ContentSha256,MimeType,ByteLength,Kind,CreatedAt,EncryptionVersion)
                    VALUES(@attachment,@session,'Bound','evidence','evidence.pdf',1,'fixture','hash','application/pdf',100,'Binary',@instant,1);
                    INSERT INTO session_attachment_index_state(AttachmentId,Status,ContentSha256,ExtractedAt,IndexedAt,PublishedGenerationId,UpdatedAt)
                    VALUES(@attachment,'Indexed','hash',@instant,@instant,'generation',@instant);
                    INSERT INTO session_attachment_chunks(ChunkId,GenerationId,SessionId,AttachmentId,LogicalKey,Version,OriginalFileName,MimeType,ContentSha256,ChunkIndex,CharacterStart,CharacterEnd,StartLine,EndLine,Content,EmbeddingDimension,ExtractedAt,IndexedAt)
                    VALUES('pdf-query-fixture','generation',@chunkSession,@attachment,'evidence',1,'evidence.pdf','application/pdf','hash',0,0,100,1,5,@content,64,@instant,@instant);
                    INSERT INTO session_attachment_embeddings(ChunkId,Embedding,Dim) VALUES('pdf-query-fixture',@vector,64);
                    """;

                seed.Parameters.AddWithValue("@session", session.ToString("D").ToUpperInvariant());

                seed.Parameters.AddWithValue("@attachment", attachment.ToString("D").ToUpperInvariant());

                seed.Parameters.AddWithValue("@chunkSession", sameCase ? session.ToString("D").ToUpperInvariant() : session.ToString("D"));

                seed.Parameters.AddWithValue("@instant", "2026-10-26T12:00:00.0000000Z");

                seed.Parameters.AddWithValue("@content", "[PDF page 1]\n" + string.Join('\n', challenge.DocumentLines));

                seed.Parameters.AddWithValue("@vector", new byte[256]);

                await seed.ExecuteNonQueryAsync();
            }

            async Task<IReadOnlyList<string[]>> Read(string sql, CancellationToken token)
            {
                await using SqliteCommand query = connection.CreateCommand();

                query.CommandText = sql;

                await using var reader = await query.ExecuteReaderAsync(token);

                List<string[]> rows = [];

                while (await reader.ReadAsync(token))
                {
                    rows.Add(Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index)
                        ? "NULL" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)!).ToArray());
                }

                return rows;
            }

            var chunks = await PdfQualificationEvidence.ReadPublishedChunksAsync(Read, session, attachment, CancellationToken.None);

            Assert.True(PdfQualificationEvidence.IndexMatches(chunks, session, attachment, "hash", 64, challenge));
        }
        finally
        {
            foreach (string owned in new[] { path, path + "-wal", path + "-shm", path + ".kdf" }) File.Delete(owned);
        }
    }
}
