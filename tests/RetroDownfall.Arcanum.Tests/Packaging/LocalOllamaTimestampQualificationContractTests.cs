using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Packaging;

[Collection("ProcessEnvironment")]
public sealed class LocalOllamaTimestampQualificationContractTests
{
    [Fact]
    public void The_real_model_receipt_requires_the_provider_free_timestamp_oracle_across_restart()
    {
        IReadOnlyList<ProductionSource> sources = ProductionSourceInventory.TestSuiteSources();

        ProductionSource harness = Assert.Single(sources,
            static source => source.Is("PublishedArcanumOllamaQualificationTests.cs"));

        Assert.Contains("PrepareTimestampProbeAsync(api, configurationDirectory)", harness.Text, StringComparison.Ordinal);

        Assert.Contains("CompleteTimestampProbeAsync(api, configurationDirectory, timestampProbe)", harness.Text, StringComparison.Ordinal);

        int prepare = harness.Text.IndexOf("PrepareTimestampProbeAsync(api, configurationDirectory)", StringComparison.Ordinal);

        int stop = harness.Text.IndexOf("await StopHostAsync(api, firstHost", StringComparison.Ordinal);

        int restart = harness.Text.IndexOf("await restartedHost.WaitForRestartReadyAsync", StringComparison.Ordinal);

        int complete = harness.Text.IndexOf("CompleteTimestampProbeAsync(api, configurationDirectory, timestampProbe)", StringComparison.Ordinal);

        int receipt = harness.Text.IndexOf("File.WriteAllTextAsync", complete, StringComparison.Ordinal);

        Assert.True(prepare < stop && stop < restart && restart < complete && complete < receipt,
            "The timestamp probe must cross the real host restart and finish before the success receipt.");

        ProductionSource oracle = Assert.Single(sources,
            static source => source.Is("PublishedArcanumOllamaQualificationTests.Timestamps.cs"));

        Assert.Contains("LongRestQualificationGrimoire.OpenAsync", oracle.Text, StringComparison.Ordinal);

        Assert.Contains("beforeCreatedAt=", oracle.Text, StringComparison.Ordinal);

        Assert.Contains("beforeId=", oracle.Text, StringComparison.Ordinal);

        Assert.Contains("api.DeleteAsync", oracle.Text, StringComparison.Ordinal);

        Assert.DoesNotContain("ping-stream", oracle.Text, StringComparison.Ordinal);

        Assert.DoesNotContain("ExecuteNonQuery", oracle.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-10-21T01:02:03.0000000Z")]
    [InlineData("2026-10-21T01:02:03.1234567Z")]
    public void Persisted_timestamp_oracle_accepts_only_the_exact_seven_fraction_UTC_grammar(string text)
    {
        Assert.True(LocalOllamaTimestampQualificationGuards.IsCanonicalPersistedInstant(text));
    }

    [Theory]
    [InlineData("2026-10-21T01:02:03.1234567+00:00")]
    [InlineData("2026-10-21T01:02:03.1234567")]
    [InlineData("2026-10-21T01:02:03.1Z")]
    [InlineData("2026-10-21T01:02:03Z")]
    [InlineData("2026-10-21T01:02:03.12345678Z")]
    [InlineData("2026-02-30T01:02:03.1234567Z")]
    [InlineData("2026-10-21T01:02:03.1234567z")]
    [InlineData(" 2026-10-21T01:02:03.1234567Z")]
    public void Persisted_timestamp_oracle_refuses_values_that_an_API_decoder_could_hide(string text)
    {
        Assert.False(LocalOllamaTimestampQualificationGuards.IsCanonicalPersistedInstant(text));
    }

    [Fact]
    public void Cursor_spellings_preserve_all_ticks_through_positive_and_negative_offsets()
    {
        DateTimeOffset instant = DateTimeOffset.Parse("2026-10-21T01:02:03.1234567+05:30", CultureInfo.InvariantCulture);

        string[] spellings = LocalOllamaTimestampQualificationGuards.CursorSpellings(instant);

        Assert.Equal(3, spellings.Distinct(StringComparer.Ordinal).Count());

        Assert.EndsWith(".1234567Z", spellings[0], StringComparison.Ordinal);

        Assert.EndsWith(".1234567+05:30", spellings[1], StringComparison.Ordinal);

        Assert.EndsWith(".1234567-07:00", spellings[2], StringComparison.Ordinal);

        Assert.All(spellings, spelling => Assert.Equal(instant,
            DateTimeOffset.Parse(spelling, CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("lost-tick")]
    [InlineData("wrong-id")]
    [InlineData("duplicate")]
    [InlineData("reordered")]
    [InlineData("missing")]
    [InlineData("extra")]
    public void Timestamp_page_oracle_rejects_identity_order_or_precision_loss(string defect)
    {
        var first = new PublishedArcanumOllamaQualificationTests.TimestampEntry(Guid.NewGuid(),
            DateTimeOffset.Parse("2026-10-21T01:02:03.1234567Z", CultureInfo.InvariantCulture));

        var second = new PublishedArcanumOllamaQualificationTests.TimestampEntry(Guid.NewGuid(), first.CreatedAt.AddTicks(-1));

        PublishedArcanumOllamaQualificationTests.TimestampEntry[] expected = [first, second];

        PublishedArcanumOllamaQualificationTests.TimestampEntry[] actual = defect switch
        {
            "lost-tick" => [first with { CreatedAt = first.CreatedAt.AddTicks(-1) }, second],
            "wrong-id" => [first with { Id = Guid.NewGuid() }, second],
            "duplicate" => [first, first],
            "reordered" => [second, first],
            "missing" => [first],
            "extra" => [first, second, second],
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        Assert.False(LocalOllamaTimestampQualificationGuards.MatchesPage(Page(actual), expected));

        Assert.True(LocalOllamaTimestampQualificationGuards.MatchesPage(Page(expected), expected));
    }

    [Fact]
    public async Task Cursor_oracle_sends_url_encoded_equivalent_instants_and_exact_deleted_identity_to_the_host()
    {
        var cursor = new PublishedArcanumOllamaQualificationTests.TimestampEntry(Guid.NewGuid(),
            DateTimeOffset.Parse("2026-10-21T01:02:03.1234567Z", CultureInfo.InvariantCulture));

        var older = new PublishedArcanumOllamaQualificationTests.TimestampEntry(Guid.NewGuid(), cursor.CreatedAt.AddTicks(-1));

        var probe = new PublishedArcanumOllamaQualificationTests.TimestampProbe(Guid.NewGuid(), cursor, [older], []);

        List<Uri> requests = [];

        using HttpClient client = new(TimestampHandler.Create(request =>
        {
            Uri uri = request.RequestUri!;

            requests.Add(uri);

            Assert.Equal(HttpMethod.Get, request.Method);

            Assert.Equal($"/api/sessions/{probe.SessionId:D}/entries", uri.AbsolutePath);

            Assert.Contains($"beforeId={cursor.Id:D}", uri.Query, StringComparison.Ordinal);

            Assert.DoesNotContain("+", uri.Query, StringComparison.Ordinal);

            string encoded = uri.Query.Split("beforeCreatedAt=", StringSplitOptions.None)[1].Split('&')[0];

            Assert.Equal(cursor.CreatedAt, DateTimeOffset.Parse(Uri.UnescapeDataString(encoded), CultureInfo.InvariantCulture));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"isSuccess\":true,\"data\":" + PageJson([older]) + "}", Encoding.UTF8, "application/json"),
            };
        })) { BaseAddress = new Uri("http://127.0.0.1:12345/") };

        await PublishedArcanumOllamaQualificationTests.AssertTimestampCursorPagesAsync(client, probe, default);

        Assert.Equal(3, requests.Count);

        Assert.Contains(requests, uri => uri.Query.Contains("%2B05%3A30", StringComparison.Ordinal));
    }

    private static JsonElement[] Page(PublishedArcanumOllamaQualificationTests.TimestampEntry[] rows)
    {
        using JsonDocument document = JsonDocument.Parse(PageJson(rows));

        return document.RootElement.EnumerateArray().Select(static row => row.Clone()).ToArray();
    }

    private static string PageJson(PublishedArcanumOllamaQualificationTests.TimestampEntry[] rows) =>
        "[" + string.Join(',', rows.Select(static row =>
            $"{{\"id\":\"{row.Id:D}\",\"createdAt\":\"{row.CreatedAt.ToString("O", CultureInfo.InvariantCulture)}\"}}")) + "]";

    private sealed class TimestampHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal static TimestampHandler Create(Func<HttpRequestMessage, HttpResponseMessage> response) => new(response);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
