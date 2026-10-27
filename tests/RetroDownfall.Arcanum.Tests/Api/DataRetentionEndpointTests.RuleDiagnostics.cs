using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class DataRetentionEndpointTests
{
    [SkippableTheory]
    [InlineData('\u2028', "\\u2028")]
    [InlineData('\u2029', "\\u2029")]
    public async Task Rejected_retention_selector_escapes_unicode_line_separators(char separator, string escaped)
    {
        RequireSqlCipher();

        await using ArcanumWebApplicationFactory factory = CreateFactory(new FakeDataRetentionService());

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PutAsync("/api/data/retention", JsonContent(
            new RetentionRuleUpdateRequest("bad" + separator + "rule", Enabled: true, Days: 30),
            ArcanumJsonContext.Default.RetentionRuleUpdateRequest));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<RetentionSettings> result = await ReadAsync(response, ArcanumJsonContext.Default.ApiResponseRetentionSettings);

        Assert.DoesNotContain(separator, result.Error!.Value.Message);

        Assert.Contains("bad" + escaped + "rule", result.Error.Value.Message, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("batch-output-files", "uploadedFiles", 2)]
    [InlineData("attachment-embeddings", "attachments", 2)]
    [InlineData("workspace-embeddings", "workspaceIndexes", 2)]
    [InlineData("cost-adjustments", "accounting", 365)]
    public async Task A_shared_alias_updates_only_its_owning_rule(string selector, string property, int days)
    {
        RequireSqlCipher();

        await using ArcanumWebApplicationFactory factory = CreateFactory(new FakeDataRetentionService());

        using HttpClient client = factory.CreateAuthenticatedClient();

        using JsonDocument before = JsonDocument.Parse(await (await client.GetAsync("/api/data/retention")).Content.ReadAsStringAsync());

        HttpResponseMessage response = await client.PutAsync("/api/data/retention", JsonContent(
            new RetentionRuleUpdateRequest(selector, Enabled: true, Days: days),
            ArcanumJsonContext.Default.RetentionRuleUpdateRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument after = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement updated = after.RootElement.GetProperty("data");

        Assert.True(updated.GetProperty(property).GetProperty("enabled").GetBoolean());

        Assert.Equal(days, updated.GetProperty(property).GetProperty("days").GetInt32());

        foreach (JsonProperty unchanged in before.RootElement.GetProperty("data").EnumerateObject().Where(item => item.Name != property))
        {
            Assert.Equal(unchanged.Value.GetRawText(), updated.GetProperty(unchanged.Name).GetRawText());
        }
    }

    [SkippableFact]
    public async Task Rejected_retention_selectors_are_bounded_and_escape_terminal_controls()
    {
        RequireSqlCipher();

        await using ArcanumWebApplicationFactory factory = CreateFactory(new FakeDataRetentionService());

        using HttpClient client = factory.CreateAuthenticatedClient();

        string selector = "bad\r\n\u001b\u202E'\\" + new string('x', 10000);

        HttpResponseMessage response = await client.PutAsync("/api/data/retention", JsonContent(
            new RetentionRuleUpdateRequest(selector, Enabled: true, Days: 30),
            ArcanumJsonContext.Default.RetentionRuleUpdateRequest));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<RetentionSettings> result = await ReadAsync(response, ArcanumJsonContext.Default.ApiResponseRetentionSettings);

        string message = result.Error!.Value.Message;

        Assert.Contains("bad\\u000D\\u000A\\u001B\\u202E\\'\\\\", message, StringComparison.Ordinal);

        Assert.Contains("...", message, StringComparison.Ordinal);

        Assert.DoesNotContain('\r', message);

        Assert.DoesNotContain('\n', message);

        Assert.DoesNotContain('\u001b', message);

        Assert.DoesNotContain('\u202E', message);

        Assert.True(message.Length < 1200);
    }

    [SkippableTheory]
    [InlineData("accounting")]
    [InlineData("inference-runs")]
    [InlineData("billable-operations")]
    [InlineData("budget-reservations")]
    [InlineData("cost-adjustments")]
    public async Task Accounting_below_its_floor_is_refused_with_the_requested_age_and_setting_without_writing(string selector)
    {
        RequireSqlCipher();

        await using ArcanumWebApplicationFactory factory = CreateFactory(new FakeDataRetentionService());

        using HttpClient client = factory.CreateAuthenticatedClient();

        RetentionSettings before = factory.Services.GetRequiredService<IDataRetentionPolicyStore>().Current;

        HttpResponseMessage response = await client.PutAsync("/api/data/retention", JsonContent(
            new RetentionRuleUpdateRequest(selector, Enabled: true, Days: 30),
            ArcanumJsonContext.Default.RetentionRuleUpdateRequest));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<RetentionSettings> result = await ReadAsync(response, ArcanumJsonContext.Default.ApiResponseRetentionSettings);

        Assert.False(result.IsSuccess);

        Assert.Equal(ErrorCodes.Data.InvalidRequest, result.Error?.Code);

        Assert.Contains("30", result.Error?.Message, StringComparison.Ordinal);

        Assert.Contains("365", result.Error?.Message, StringComparison.Ordinal);

        Assert.Contains("Arcanum:Retention:AccountingMinimumDays", result.Error?.Message, StringComparison.Ordinal);

        RetentionSettings after = factory.Services.GetRequiredService<IDataRetentionPolicyStore>().Current;

        Assert.Equal(before.Accounting, after.Accounting);

        Assert.Equal(before.AccountingMinimumDays, after.AccountingMinimumDays);
    }

    [SkippableTheory]
    [InlineData("missing-policy", "not recognized")]
    [InlineData("tapestry", "no configurable")]
    public async Task Retention_rule_refusals_identify_the_selector_and_offer_configurable_choices(string selector, string reason)
    {
        RequireSqlCipher();

        await using ArcanumWebApplicationFactory factory = CreateFactory(new FakeDataRetentionService());

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PutAsync("/api/data/retention", JsonContent(
            new RetentionRuleUpdateRequest(selector, Enabled: true, Days: 30),
            ArcanumJsonContext.Default.RetentionRuleUpdateRequest));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<RetentionSettings> result = await ReadAsync(response, ArcanumJsonContext.Default.ApiResponseRetentionSettings);

        Assert.Contains(selector, result.Error?.Message, StringComparison.Ordinal);

        Assert.Contains(reason, result.Error?.Message, StringComparison.Ordinal);

        Assert.Contains("archived-sessions", result.Error?.Message, StringComparison.Ordinal);

        Assert.Contains("uploaded-files", result.Error?.Message, StringComparison.Ordinal);

        Assert.Contains("accounting", result.Error?.Message, StringComparison.Ordinal);
    }
}
