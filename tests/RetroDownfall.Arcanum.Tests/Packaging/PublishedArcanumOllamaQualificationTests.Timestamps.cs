using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed partial class PublishedArcanumOllamaQualificationTests
{
    private static async Task<TimestampProbe> PrepareTimestampProbeAsync(HttpClient api, string configurationDirectory)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45));

        using HttpResponseMessage create = await api.PostAsync("api/sessions",
            JsonContent.Create(new CreateSessionRequest(null, "Timestamp qualification"),
                ArcanumJsonContext.Default.CreateSessionRequest), deadline.Token);

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using JsonDocument created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(deadline.Token));

        Guid sessionId = created.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        List<TimestampEntry> appended = [];

        for (int index = 0; index < 4; index++)
        {
            using HttpResponseMessage append = await api.PostAsync($"api/sessions/{sessionId:D}/entries",
                JsonContent.Create(new AppendEntryRequest(MessageRole.User, $"Timestamp qualification {index}"),
                    ArcanumJsonContext.Default.AppendEntryRequest), deadline.Token);

            Assert.Equal(HttpStatusCode.OK, append.StatusCode);

            using JsonDocument entry = JsonDocument.Parse(await append.Content.ReadAsStringAsync(deadline.Token));

            appended.Add(ReadTimestampEntry(entry.RootElement.GetProperty("data")));
        }

        JsonElement[] rows = await ReadTimestampPageAsync(api, $"api/sessions/{sessionId:D}/entries?limit=100", deadline.Token);

        TimestampEntry[] ordered = rows.Select(ReadTimestampEntry).ToArray();

        Assert.Equal(4, ordered.Length);

        Assert.Equal(appended.OrderBy(static row => row.Id), ordered.OrderBy(static row => row.Id));

        TimestampEntry cursor = ordered[1];

        TimestampProbe probe = new(sessionId, cursor, ordered[2..], []);

        using HttpResponseMessage delete = await api.DeleteAsync(
            $"api/sessions/{sessionId:D}/entries/{cursor.Id:D}", deadline.Token);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        await AssertTimestampCursorPagesAsync(api, probe, deadline.Token);

        Dictionary<Guid, string> persisted = await AssertPersistedTimestampEncodingAsync(configurationDirectory, deadline.Token);

        Assert.DoesNotContain(cursor.Id, persisted.Keys);

        foreach (TimestampEntry entry in ordered.Where(row => row.Id != cursor.Id))
        {
            Assert.Equal(entry.CreatedAt, DateTimeOffset.ParseExact(persisted[entry.Id],
                LocalOllamaTimestampQualificationGuards.PersistedFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
        }

        return probe with { PersistedEntries = persisted };
    }

    private static async Task CompleteTimestampProbeAsync(HttpClient api, string configurationDirectory, TimestampProbe probe)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45));

        await AssertTimestampCursorPagesAsync(api, probe, deadline.Token);

        Dictionary<Guid, string> persisted = await AssertPersistedTimestampEncodingAsync(configurationDirectory, deadline.Token);

        Assert.Equal(probe.PersistedEntries.OrderBy(static row => row.Key), persisted.OrderBy(static row => row.Key));

        using HttpResponseMessage delete = await api.DeleteAsync($"api/sessions/{probe.SessionId:D}", deadline.Token);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    internal static async Task AssertTimestampCursorPagesAsync(HttpClient api, TimestampProbe probe, CancellationToken cancellationToken)
    {
        foreach (string spelling in LocalOllamaTimestampQualificationGuards.CursorSpellings(probe.Cursor.CreatedAt))
        {
            string path = $"api/sessions/{probe.SessionId:D}/entries?limit=100"
                + $"&beforeCreatedAt={Uri.EscapeDataString(spelling)}&beforeId={probe.Cursor.Id:D}";

            JsonElement[] page = await ReadTimestampPageAsync(api, path, cancellationToken);

            Assert.True(LocalOllamaTimestampQualificationGuards.MatchesPage(page, probe.ExpectedPage),
                "The deleted Entry's timestamp cursor must preserve exact older row identities, order and ticks for every offset spelling.");
        }
    }

    private static async Task<JsonElement[]> ReadTimestampPageAsync(HttpClient api, string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await api.GetAsync(path, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        Assert.True(document.RootElement.GetProperty("isSuccess").GetBoolean(),
            "The timestamp page response must report success.");

        return document.RootElement.GetProperty("data").EnumerateArray().Select(static row => row.Clone()).ToArray();
    }

    private static TimestampEntry ReadTimestampEntry(JsonElement row) =>
        new(row.GetProperty("id").GetGuid(), row.GetProperty("createdAt").GetDateTimeOffset());

    private static async Task<Dictionary<Guid, string>> AssertPersistedTimestampEncodingAsync(
        string configurationDirectory, CancellationToken cancellationToken = default)
    {
        LongRestQualificationGrimoire grimoire = await LongRestQualificationGrimoire.OpenAsync(configurationDirectory, cancellationToken);

        // Read the production host's actual stored text, independently of API date serialization.
        // The existing helper opens SQLCipher read-only using only this isolated installation's key ring.
        IReadOnlyList<string[]> rows = await grimoire.DumpAsync("""
            SELECT 'Entries.CreatedAt', Id, CreatedAt, typeof(CreatedAt) FROM Entries
            UNION ALL SELECT 'Sessions.CreatedAt', Id, CreatedAt, typeof(CreatedAt) FROM Sessions
            UNION ALL SELECT 'Sessions.UpdatedAt', Id, UpdatedAt, typeof(UpdatedAt) FROM Sessions
            UNION ALL SELECT 'SessionAttachments.CreatedAt', Id, CreatedAt, typeof(CreatedAt) FROM SessionAttachments
            UNION ALL SELECT 'SessionAttachments.SourceLastWriteAt', Id, SourceLastWriteAt, typeof(SourceLastWriteAt)
                FROM SessionAttachments WHERE SourceLastWriteAt IS NOT NULL
            UNION ALL SELECT 'SessionContextPins.CreatedAt', Id, CreatedAt, typeof(CreatedAt) FROM SessionContextPins
            UNION ALL SELECT 'SessionContextPins.UpdatedAt', Id, UpdatedAt, typeof(UpdatedAt) FROM SessionContextPins
            UNION ALL SELECT 'InferenceRuns.StartedAt', Id, StartedAt, typeof(StartedAt) FROM InferenceRuns
            UNION ALL SELECT 'InferenceRuns.CompletedAt', Id, CompletedAt, typeof(CompletedAt)
                FROM InferenceRuns WHERE CompletedAt IS NOT NULL
            UNION ALL SELECT 'BillableOperations.StartedAt', Id, StartedAt, typeof(StartedAt) FROM BillableOperations
            UNION ALL SELECT 'BillableOperations.CompletedAt', Id, CompletedAt, typeof(CompletedAt) FROM BillableOperations;
            """, cancellationToken);

        Assert.NotEmpty(rows);

        Dictionary<Guid, string> entries = [];

        foreach (string[] row in rows)
        {
            Assert.True(row[3] == "text" && LocalOllamaTimestampQualificationGuards.IsCanonicalPersistedInstant(row[2]),
                $"The real host persisted a noncanonical instant in {row[0]}.");

            if (row[0] == "Entries.CreatedAt")
            {
                entries.Add(Guid.Parse(row[1]), row[2]);
            }
        }

        Assert.NotEmpty(entries);

        return entries;
    }

    internal sealed record TimestampEntry(Guid Id, DateTimeOffset CreatedAt);

    internal sealed record TimestampProbe(Guid SessionId, TimestampEntry Cursor, TimestampEntry[] ExpectedPage,
        Dictionary<Guid, string> PersistedEntries);
}

internal static class LocalOllamaTimestampQualificationGuards
{
    internal const string PersistedFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    internal static bool IsCanonicalPersistedInstant(string value) =>
        value.Length == 28 && value[^1] == 'Z'
            && DateTimeOffset.TryParseExact(value, PersistedFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);

    internal static string[] CursorSpellings(DateTimeOffset instant) =>
    [
        instant.ToUniversalTime().ToString(PersistedFormat, CultureInfo.InvariantCulture),
        instant.ToOffset(TimeSpan.FromMinutes(330)).ToString("O", CultureInfo.InvariantCulture),
        instant.ToOffset(TimeSpan.FromHours(-7)).ToString("O", CultureInfo.InvariantCulture),
    ];

    internal static bool MatchesPage(JsonElement[] actual, PublishedArcanumOllamaQualificationTests.TimestampEntry[] expected)
    {
        if (actual.Length != expected.Length)
        {
            return false;
        }

        for (int index = 0; index < actual.Length; index++)
        {
            if (!actual[index].TryGetProperty("id", out JsonElement id) || !id.TryGetGuid(out Guid parsedId)
                || parsedId != expected[index].Id
                || !actual[index].TryGetProperty("createdAt", out JsonElement at)
                || !at.TryGetDateTimeOffset(out DateTimeOffset parsedAt) || parsedAt != expected[index].CreatedAt)
            {
                return false;
            }
        }

        return true;
    }
}
