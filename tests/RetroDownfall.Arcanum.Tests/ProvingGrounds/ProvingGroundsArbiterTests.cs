using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

public sealed class ProvingGroundsArbiterTests
{

    [Fact]
    public async Task RegexInquisitor_MatchAndShouldMatch_Passes()
    {

        ProvingGroundsArbiter arbiter = CreateArbiter(new FakeIntelligenceProvider());

        IReadOnlyList<InquisitorVerdict> verdicts = await arbiter.AdjudicateAsync(
            "Hello world",
            [new RegexInquisitor("hello", ShouldMatch: true, IgnoreCase: true)],
            judgeModel: null);

        Assert.Single(verdicts);

        Assert.True(verdicts[0].Passed);

        Assert.Equal("regex", verdicts[0].Kind);

    }

    [Fact]
    public async Task RegexInquisitor_ShouldNotMatch_PassesWhenAbsent()
    {

        ProvingGroundsArbiter arbiter = CreateArbiter(new FakeIntelligenceProvider());

        IReadOnlyList<InquisitorVerdict> verdicts = await arbiter.AdjudicateAsync(
            "clean output",
            [new RegexInquisitor("error", ShouldMatch: false)],
            judgeModel: null);

        Assert.True(verdicts[0].Passed);

    }

    [Fact]
    public async Task RegexInquisitor_InvalidPattern_Fails()
    {

        ProvingGroundsArbiter arbiter = CreateArbiter(new FakeIntelligenceProvider());

        IReadOnlyList<InquisitorVerdict> verdicts = await arbiter.AdjudicateAsync(
            "text",
            [new RegexInquisitor("([", ShouldMatch: true)],
            judgeModel: null);

        Assert.False(verdicts[0].Passed);

        Assert.Contains("Invalid regex", verdicts[0].Detail, StringComparison.Ordinal);

    }

    [Fact]
    public async Task JsonSchemaInquisitor_ValidJsonWithRequiredProperty_Passes()
    {

        ProvingGroundsArbiter arbiter = CreateArbiter(new FakeIntelligenceProvider());

        JsonElement schema = JsonDocument.Parse("""{"type":"object","required":["name"],"properties":{"name":{"type":"string"}}}""").RootElement;

        IReadOnlyList<InquisitorVerdict> verdicts = await arbiter.AdjudicateAsync(
            """{"name":"Arcanum"}""",
            [new JsonSchemaInquisitor(schema)],
            judgeModel: null);

        Assert.True(verdicts[0].Passed);

    }

    [Fact]
    public async Task JsonSchemaInquisitor_MissingRequiredProperty_Fails()
    {

        ProvingGroundsArbiter arbiter = CreateArbiter(new FakeIntelligenceProvider());

        JsonElement schema = JsonDocument.Parse("""{"required":["name"]}""").RootElement;

        IReadOnlyList<InquisitorVerdict> verdicts = await arbiter.AdjudicateAsync(
            """{"other":1}""",
            [new JsonSchemaInquisitor(schema)],
            judgeModel: null);

        Assert.False(verdicts[0].Passed);

        Assert.Contains("name", verdicts[0].Detail, StringComparison.Ordinal);

    }

    [Fact]
    public async Task JsonSchemaInquisitor_TypeMismatch_Fails()
    {

        ProvingGroundsArbiter arbiter = CreateArbiter(new FakeIntelligenceProvider());

        JsonElement schema = JsonDocument.Parse("""{"properties":{"count":{"type":"number"}}}""").RootElement;

        IReadOnlyList<InquisitorVerdict> verdicts = await arbiter.AdjudicateAsync(
            """{"count":"not-a-number"}""",
            [new JsonSchemaInquisitor(schema)],
            judgeModel: null);

        Assert.False(verdicts[0].Passed);

    }

    [Fact]
    public async Task JsonSchemaInquisitor_MalformedOutput_Fails()
    {

        ProvingGroundsArbiter arbiter = CreateArbiter(new FakeIntelligenceProvider());

        IReadOnlyList<InquisitorVerdict> verdicts = await arbiter.AdjudicateAsync(
            "not json",
            [new JsonSchemaInquisitor(JsonDocument.Parse("{}").RootElement)],
            judgeModel: null);

        Assert.False(verdicts[0].Passed);

    }

    [Fact]
    public async Task SemanticInquisitor_YesAnswer_WhenExpectedTrue_Passes()
    {

        FakeIntelligenceProvider provider = new() { NextText = "YES" };

        ProvingGroundsArbiter arbiter = CreateArbiter(provider);

        IReadOnlyList<InquisitorVerdict> verdicts = await arbiter.AdjudicateAsync(
            "Certainly, I would be happy to help.",
            [new SemanticInquisitor("Is the response polite?", ExpectedAnswer: true)],
            judgeModel: "fast-model");

        Assert.True(verdicts[0].Passed);

        Assert.Equal("semantic", verdicts[0].Kind);

    }

    [Fact]
    public async Task SemanticInquisitor_NoAnswer_WhenExpectedTrue_Fails()
    {

        FakeIntelligenceProvider provider = new() { NextText = "NO" };

        ProvingGroundsArbiter arbiter = CreateArbiter(provider);

        IReadOnlyList<InquisitorVerdict> verdicts = await arbiter.AdjudicateAsync(
            "Go away.",
            [new SemanticInquisitor("Is the response polite?", ExpectedAnswer: true)],
            judgeModel: "fast-model");

        Assert.False(verdicts[0].Passed);

    }

    [Fact]
    public async Task SemanticInquisitor_InferenceFailure_Fails()
    {

        FakeIntelligenceProvider provider = new() { NextFailure = new Error("Hub.Model", "model unavailable") };

        ProvingGroundsArbiter arbiter = CreateArbiter(provider);

        IReadOnlyList<InquisitorVerdict> verdicts = await arbiter.AdjudicateAsync(
            "output",
            [new SemanticInquisitor("Is it good?")],
            judgeModel: "fast-model");

        Assert.False(verdicts[0].Passed);

        Assert.Contains("inference failed", verdicts[0].Detail, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task AdjudicateAsync_CancelledToken_ThrowsBetweenInquisitors()
    {

        ProvingGroundsArbiter arbiter = CreateArbiter(new FakeIntelligenceProvider());

        using CancellationTokenSource cts = new();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => arbiter.AdjudicateAsync("text", [new RegexInquisitor("a")], judgeModel: null, cts.Token));

    }

    [Fact]
    public async Task JsonSchemaInquisitor_UnknownDeclaredType_FailsClosed()
    {

        ProvingGroundsArbiter arbiter = CreateArbiter(new FakeIntelligenceProvider());

        JsonElement schema = JsonDocument.Parse("""{"properties":{"x":{"type":"mystery"}}}""").RootElement;

        IReadOnlyList<InquisitorVerdict> verdicts = await arbiter.AdjudicateAsync(
            """{"x":"hi"}""",
            [new JsonSchemaInquisitor(schema)],
            judgeModel: null);

        Assert.False(verdicts[0].Passed);

    }

    private static ProvingGroundsArbiter CreateArbiter(
        IArcanumIntelligenceProvider intelligence)
    {
        return new ProvingGroundsArbiter(intelligence);

    }

    private sealed class FakeIntelligenceProvider : IArcanumIntelligenceProvider
    {

        public string NextText { get; init; } = "YES";

        public Error? NextFailure { get; init; }

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            CancellationToken cancellationToken = default,
            InferenceAuditContext? auditContext = null)
        {

            if (NextFailure is Error failure)
            {
                return Task.FromResult(Result<PromptTurnResult>.Failure(failure));
            }

            return Task.FromResult(
                Result<PromptTurnResult>.Success(new PromptTurnResult(NextText, null)));

        }

        public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default,
            InferenceAuditContext? auditContext = null)
        {

            await Task.CompletedTask.ConfigureAwait(false);

            yield break;

        }

    }

    private sealed class TestOptionsMonitor(ArcanumSettings current) : IOptionsMonitor<ArcanumSettings>
    {

        public ArcanumSettings CurrentValue => current;

        public ArcanumSettings Get(string? name) => current;

        public IDisposable OnChange(Action<ArcanumSettings, string?> listener) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {

            public void Dispose()
            {
            }

        }

    }


    /// <summary>
    /// The trial output is clamped before adjudication. A raw char slice can leave a lone high
    /// surrogate at the boundary, and JsonDocument.Parse throws ArgumentException — not JsonException
    /// — while transcoding invalid UTF-16, escaping AdjudicateJsonSchema's catch and losing the whole
    /// trial run to a generic 500.
    /// </summary>
    [Fact]
    public async Task Clamped_output_never_ends_on_a_split_surrogate_pair()
    {

        int maxOutputChars = ArcanumSettingClamps.MaxPingPromptChars(
            ArcanumRuntimeDefaults.Intelligence.MaxPingPromptChars);

        // Pad so the astral-plane character straddles the clamp boundary exactly.
        string output = new string('a', maxOutputChars - 1) + "\U0001F600" + new string('b', 16);

        ProvingGroundsArbiter arbiter = CreateArbiter(new FakeIntelligenceProvider());

        IReadOnlyList<InquisitorVerdict> verdicts = await arbiter.AdjudicateAsync(
            output,
            [new JsonSchemaInquisitor(JsonDocument.Parse("""{"type":"object"}""").RootElement)],
            judgeModel: null);

        Assert.Single(verdicts);

        Assert.False(verdicts[0].Passed);

    }

}
