using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// Embedding-Bounded Summary. The recipe carries a summary byte bound derived from the embedding provider's window with the estimator's own
// worst-case arithmetic; the summary request states it, so an answer that respects it always fits its embedding request. The request's output
// cap stays the fixed answer reservation at every bound: a byte bound cannot prove a token count, so a cap derived from it would cut off an
// answer that is within the bound. The embedding window gate stays the last guard for a model that ignores the bound, and counts that case.
public sealed class LongRestSummaryBoundTests
{
    private const string RefusedMetric = "arcanum.long_rest.summary.embedding_window_refused";

    private static readonly ModelTokenEstimator Estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

    private static readonly CovenantDigest Zero = new(new byte[32]);

    // Independent arithmetic: the largest byte count whose worst-case embedding charge (one token per byte for a model the estimator does not
    // know, plus the 15% margin rounded up) and 16 framing tokens still fit the window, capped at the 8,192-byte code-owned maximum.
    private static long WorstCaseCharge(long bytes) => bytes + (bytes * 15 + 99) / 100;

    private static int LargestFittingBytes(int window)
    {
        int low = 0;

        int high = LongRestDiscoveryLimits.SummaryUtf8Bytes;

        while (low < high)
        {
            int middle = (low + high + 1) / 2;

            if (WorstCaseCharge(middle) + LongRestPromptLayout.FramingTokens <= window)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    public static TheoryData<int, int> Bounds => new()
    {
        { int.MinValue, 8192 },

        { -1, 8192 },

        { 0, 8192 },

        { 1, 208 },

        { 256, 208 },

        { 1193, 1023 },

        { 1194, 1024 },

        { 1195, 1025 },

        { 2047, 1766 },

        { 2048, 1766 },

        { 2049, 1767 },

        { 2371, 2047 },

        { 2372, 2048 },

        { 2373, 2049 },

        { 4096, 3547 },

        { 8192, 7109 },

        { 9436, 8191 },

        { 9437, 8192 },

        { 9438, 8192 },

        { 32768, 8192 },

        { 2_097_152, 8192 },

        { int.MaxValue, 8192 },
    };

    [Theory]
    [MemberData(nameof(Bounds))]
    public void The_summary_bound_is_the_embedding_window_less_framing_over_the_worst_case_tokens_per_byte(int window, int expected) =>
        Assert.Equal(expected, LongRestPromptLayout.SummaryBoundUtf8Bytes(window));

    [Fact]
    public void The_smallest_recipe_window_gets_the_smallest_legal_bound_and_the_default_window_a_bound_below_the_maximum()
    {
        Assert.Equal(1194, LongRestPromptLayout.MinimumEmbeddingWindowTokens);

        Assert.Equal(1024, LongRestPromptLayout.MinimumSummaryUtf8Bytes);

        Assert.Equal(LongRestPromptLayout.MinimumSummaryUtf8Bytes, LongRestPromptLayout.SummaryBoundUtf8Bytes(LongRestPromptLayout.MinimumEmbeddingWindowTokens));

        // The default 8,192-token window is below the 9,437 tokens a maximum summary needs, which is what the bound exists for.
        Assert.Equal(7109, LongRestPromptLayout.SummaryBoundUtf8Bytes(new ProviderSettings().ContextWindowLimit));

        Assert.True(LongRestPromptLayout.SummaryBoundUtf8Bytes(9437) == LongRestDiscoveryLimits.SummaryUtf8Bytes
            && LongRestPromptLayout.SummaryBoundUtf8Bytes(9436) < LongRestDiscoveryLimits.SummaryUtf8Bytes);
    }

    [Fact]
    public void The_bound_is_the_largest_size_whose_worst_case_charge_and_framing_fit_every_window_from_the_floor_to_40000_tokens()
    {
        int previous = 0;

        for (int window = LongRestPromptLayout.MinimumEmbeddingWindowTokens; window <= 40000; window++)
        {
            int bound = LongRestPromptLayout.SummaryBoundUtf8Bytes(window);

            Assert.Equal(LargestFittingBytes(window), bound);

            Assert.True(bound >= previous, $"window {window}");

            Assert.True(bound >= LongRestPromptLayout.MinimumSummaryUtf8Bytes && bound <= LongRestDiscoveryLimits.SummaryUtf8Bytes, $"window {window}");

            Assert.True(WorstCaseCharge(bound) + LongRestPromptLayout.FramingTokens <= window, $"window {window}");

            previous = bound;
        }
    }

    // Windows from the static minimum to 32k: every boundary, and a stride that visits every residue of the arithmetic.
    public static IEnumerable<object[]> EstimatorWindows()
    {
        HashSet<int> windows = [1194, 1195, 2047, 2048, 2049, 2372, 2373, 4096, 6775, 8191, 8192, 8193, 9436, 9437, 9438, 16384, 32767, 32768];

        for (int window = 1200; window <= 32768; window += 509)
        {
            windows.Add(window);
        }

        foreach (int window in windows.Order())
        {
            yield return [window, false];

            yield return [window, true];
        }
    }

    private static ProviderSettings EmbeddingProvider(int window, bool catalog) => new()
    {
        Name = "embedding-provider",
        Type = AiProviderKind.OpenAICompatible,
        Endpoint = catalog ? "https://api.openai.com/v1" : "https://provider.test/v1",
        Models = [catalog ? "gpt-4o" : "embedding"],
        ContextWindowLimit = window,
    };

    private static LongRestProviderPayload EmbeddingPayload(string text) =>
        LongRestProviderPayload.Embedding(LongRestPackingData.BaseWork(LongRestWorkKind.HierarchyFold, "fold-1", LongRestPackingData.Digest), text).Value;

    [Theory]
    [MemberData(nameof(EstimatorWindows))]
    public void A_summary_of_exactly_the_bound_passes_the_real_embedding_estimator_and_the_window_gate_and_one_byte_more_is_not_slack(int window, bool catalog)
    {
        int bound = LongRestPromptLayout.SummaryBoundUtf8Bytes(window);

        ProviderSettings provider = EmbeddingProvider(window, catalog);

        string model = catalog ? "gpt-4o" : "embedding";

        // Single-byte characters are the estimator's worst case for a model it does not know (one token per byte); a mixed-width text
        // is a tokenizer's worst case. Both are exactly the bound in UTF-8 bytes.
        string[] texts = [new string('a', bound), LongRestPackingData.Content(new Random(window), bound)];

        foreach (string text in texts)
        {
            Assert.Equal(bound, new UTF8Encoding(false, true).GetByteCount(text));

            int tokens = Estimator.EstimateLongRestEmbedding(provider, model, EmbeddingPayload(text)).TokenCount;

            Assert.True(tokens + LongRestPromptLayout.FramingTokens <= window, $"window {window}: {tokens} tokens plus framing");

            Assert.True(ModelCallExecutor.CheckLongRestWindow(new LongRestProviderAdmission.Embedding(provider, model, 64, tokens, Zero)).IsSuccess);
        }

        // Negative twin: the bound is tight for a model the estimator does not know. A byte more is charged past the window, so a bound derived
        // from fewer tokens per byte would not be safe.
        if (!catalog && bound < LongRestDiscoveryLimits.SummaryUtf8Bytes)
        {
            int over = Estimator.EstimateLongRestEmbedding(provider, model, EmbeddingPayload(new string('a', bound + 1))).TokenCount;

            Assert.True(over + LongRestPromptLayout.FramingTokens > window, $"window {window}: bound {bound} is not tight");
        }
    }

    [Fact]
    public void A_maximum_summary_is_not_safe_below_the_window_the_bound_gives_it_to_and_the_gate_counts_the_refusal()
    {
        ProviderSettings provider = EmbeddingProvider(8192, catalog: false);

        int tokens = Estimator.EstimateLongRestEmbedding(provider, "embedding", EmbeddingPayload(new string('a', LongRestDiscoveryLimits.SummaryUtf8Bytes))).TokenCount;

        // The shipped residual, now stated as what the bound prevents: a maximum answer is charged past the default window.
        Assert.True(tokens > 8192);

        using AsyncFlowMetricCapture metrics = new(RefusedMetric);

        Result refused = ModelCallExecutor.CheckLongRestWindow(new LongRestProviderAdmission.Embedding(provider, "embedding", 64, tokens, Zero));

        Assert.Equal(ErrorCodes.LongRest.ModelContextTooSmall, refused.Error.Code);

        Assert.Equal(1, metrics.Sum(RefusedMetric));

        // A summary that fits, an unset window and an invalid admission count nothing.
        Assert.True(ModelCallExecutor.CheckLongRestWindow(new LongRestProviderAdmission.Embedding(provider, "embedding", 64, 8192, Zero)).IsSuccess);

        Assert.True(ModelCallExecutor.CheckLongRestWindow(new LongRestProviderAdmission.Embedding(EmbeddingProvider(0, false), "embedding", 64, 100000, Zero)).IsSuccess);

        Assert.Equal(ErrorCodes.LongRest.InvalidAdmission, ModelCallExecutor.CheckLongRestWindow(new LongRestProviderAdmission.Embedding(provider, "embedding", 64, 0, Zero)).Error.Code);

        Assert.Equal(1, metrics.Sum(RefusedMetric));
    }

    [Fact]
    public void The_summary_prompt_states_the_bound_as_four_digits_so_every_legal_bound_renders_to_the_same_size()
    {
        string prompt = LongRestPromptLayout.SummarySystemPromptFor(7109);

        Assert.StartsWith(LongRestPromptLayout.SummarySystemPrompt, prompt, StringComparison.Ordinal);

        Assert.Equal(LongRestPromptLayout.SummarySystemPrompt + " The summary must not exceed 7109 bytes of UTF-8 text; omit detail rather than exceed it.", prompt);

        int bytes = Encoding.UTF8.GetByteCount(prompt);

        for (int bound = LongRestPromptLayout.MinimumSummaryUtf8Bytes; bound <= LongRestDiscoveryLimits.SummaryUtf8Bytes; bound++)
        {
            string rendered = LongRestPromptLayout.SummarySystemPromptFor(bound);

            Assert.Equal(bytes, Encoding.UTF8.GetByteCount(rendered));

            Assert.Contains(" " + bound.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes", rendered, StringComparison.Ordinal);
        }

        foreach (int illegal in new[] { int.MinValue, 0, 1023, 8193, int.MaxValue })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => LongRestPromptLayout.SummarySystemPromptFor(illegal));
        }

        // The request's fixed bytes are measured once, and are exactly what any recipe's prompt, schemas and names take.
        foreach (int bound in new[] { 1024, 2048, 5000, 7109, 8192 })
        {
            Assert.Equal(LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Summary), LongRestPromptLayout.RenderedBytes(
                LongRestPromptLayout.SummarySystemPromptFor(bound), string.Empty, LongRestPromptLayout.SummarySchema, LongRestPromptLayout.SummarySchemaName,
                LongRestPromptLayout.SummarySchemaDescription));
        }
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(1766)]
    [InlineData(2048)]
    [InlineData(2049)]
    [InlineData(5000)]
    [InlineData(7109)]
    [InlineData(8191)]
    [InlineData(8192)]
    public void The_summary_answer_cap_is_the_fixed_reservation_at_every_bound_and_the_call_reserves_exactly_that(int bound)
    {
        LongRestProviderPayload payload = LongRestPackingData.SummaryPayload(TwoChildren(), LongRestPackingData.Digest, 5328, bound).Value;

        LongRestChatRequest request = LongRestProviderCallFreezer.RenderChat(payload).Value;

        // The bound is a stated instruction. It never lowers the cap, because a cap derived from bytes would cut off an in-bound answer.
        Assert.Equal(LongRestPromptLayout.ReservedAnswerTokens, request.MaxOutputTokens);

        Assert.Equal(LongRestPromptLayout.ReservedAnswerTokens, Estimator.EstimateLongRestChat(EmbeddingProvider(8192, false), "model", payload).ReservedAnswerTokens);
    }

    // Identifier, path and timestamp dense text is the least compressible a summary realistically is (about 2.3 bytes per o200k_base token),
    // so it is the case where a cap derived from the byte bound would cut a within-bound answer off and park the sweep for an operator.
    private static string DenseSummary(int bytes)
    {
        StringBuilder text = new();

        for (int i = 0; Encoding.UTF8.GetByteCount(text.ToString()) < bytes; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"2026-10-{1 + i % 28:D2}T21:17:{i % 60:D2}Z /src/Api/Tower/Enricher{i}.cs 7E1D9C42-{i % 9999:D4}-4F63-9A0E-3C8B27D641F5 ");
        }

        return text.ToString()[..bytes].TrimEnd();
    }

    [Fact]
    public void A_within_bound_answer_dense_in_identifiers_fits_the_cap_at_the_smallest_bound()
    {
        int bound = LongRestPromptLayout.MinimumSummaryUtf8Bytes;

        string answer = "{\"summary\":\"" + DenseSummary(bound) + "\"}";

        // Every character is ASCII and the answer is at most the bound plus the JSON wrapper, so this is a within-bound answer.
        Assert.True(Encoding.UTF8.GetByteCount(answer) <= bound + 16);

        int tokens = new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance).ResolveTokenizer(null).CountTokens(answer);

        // Guard: the sample must be dense enough to cut off a cap of a quarter of the bound (512 tokens), or this test proves nothing.
        Assert.True(tokens > bound / 4, $"sample is only {tokens} tokens");

        LongRestProviderPayload payload = LongRestPackingData.SummaryPayload(TwoChildren(), LongRestPackingData.Digest, 5328, bound).Value;

        Assert.True(tokens <= LongRestProviderCallFreezer.RenderChat(payload).Value.MaxOutputTokens,
            $"{tokens} tokens would be cut off by a cap of {LongRestProviderCallFreezer.RenderChat(payload).Value.MaxOutputTokens}");
    }

    private static ImmutableArray<LongRestHierarchyChild> TwoChildren() =>
        [LongRestPackingData.Child(0, "first child"), LongRestPackingData.Child(1, "second child")];

    [Fact]
    public void A_summary_payload_carries_its_bound_and_refuses_an_illegal_bound()
    {
        LongRestProviderPayload payload = LongRestPackingData.SummaryPayload(TwoChildren(), LongRestPackingData.Digest, 5328, 7109).Value;

        Assert.Equal(7109, payload.SummaryBoundBytes);

        LongRestProviderPayload defaulted = LongRestPackingData.SummaryPayload(TwoChildren(), LongRestPackingData.Digest, 5328).Value;

        Assert.Equal(LongRestDiscoveryLimits.SummaryUtf8Bytes, defaulted.SummaryBoundBytes);

        foreach (int illegal in new[] { int.MinValue, 0, 1023, 8193, int.MaxValue })
        {
            Assert.True(LongRestPackingData.SummaryPayload(TwoChildren(), LongRestPackingData.Digest, 5328, illegal).IsFailure, $"bound {illegal}");
        }

        // An adjudication has no summary bound.
        LongRestProviderPayload adjudication = LongRestPackingData.AdjudicationPayload([LongRestPackingData.Leaf(0, "a"), LongRestPackingData.Leaf(1, "b")], 5328).Value;

        Assert.Equal(0, adjudication.SummaryBoundBytes);
    }

    [Fact]
    public void The_summary_request_states_the_bound_in_its_prompt_and_a_different_bound_is_a_different_request()
    {
        LongRestProviderPayload bounded = LongRestPackingData.SummaryPayload(TwoChildren(), LongRestPackingData.Digest, 5328, 7109).Value;

        LongRestChatRequest request = LongRestProviderCallFreezer.RenderChat(bounded).Value;

        Assert.Equal(LongRestPromptLayout.SummarySystemPromptFor(7109), request.SystemPrompt);

        Assert.Contains("must not exceed 7109 bytes", request.SystemPrompt, StringComparison.Ordinal);

        Assert.Equal(LongRestPromptLayout.ReservedAnswerTokens, request.MaxOutputTokens);

        Assert.Equal(LongRestPromptLayout.SummaryObservation(1, "first child") + LongRestPromptLayout.SummaryObservation(2, "second child"), request.UserPrompt);

        Assert.Equal(LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Summary) + LongRestPromptLayout.SummaryObservationBytes("first child")
            + LongRestPromptLayout.SummaryObservationBytes("second child"),
            LongRestPromptLayout.RenderedBytes(request.SystemPrompt, request.UserPrompt, request.SchemaJson, request.SchemaName, request.SchemaDescription));

        // The bound is part of the exact request, so it is part of the payload digest the attempt and the receipt bind.
        ProviderSettings provider = EmbeddingProvider(8192, false);

        LongRestProviderPayload maximum = LongRestPackingData.SummaryPayload(TwoChildren(), LongRestPackingData.Digest, 5328).Value;

        Assert.Equal(LongRestPromptLayout.ReservedAnswerTokens, LongRestProviderCallFreezer.RenderChat(maximum).Value.MaxOutputTokens);

        Assert.NotEqual(LongRestProviderCallFreezer.PayloadDigest(bounded, provider, "model").Value, LongRestProviderCallFreezer.PayloadDigest(maximum, provider, "model").Value);

        // The estimator reserves exactly the cap the request carries.
        Assert.Equal(LongRestPromptLayout.ReservedAnswerTokens, Estimator.EstimateLongRestChat(provider, "model", bounded).ReservedAnswerTokens);
    }

    private static LongRestRecipeIdentity Recipe(int summaryBound, int budget = 5328) =>
        new LongRestRecipeIdentity(1, 1, "summary-provider", "summary-model", Zero, "embedding-provider", "embedding-model", Zero, 17)
        {
            ProviderInputUtf8Bytes = budget,
            SummaryUtf8Bytes = summaryBound,
        };

    [Theory]
    [InlineData(1024, true)]
    [InlineData(1025, true)]
    [InlineData(1766, true)]
    [InlineData(2048, true)]
    [InlineData(2049, true)]
    [InlineData(7109, true)]
    [InlineData(8191, true)]
    [InlineData(8192, true)]
    [InlineData(1023, false)]
    [InlineData(999, false)]
    [InlineData(8193, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(int.MinValue, false)]
    [InlineData(int.MaxValue, false)]
    public void A_recipe_accepts_exactly_the_summary_bounds_from_the_smallest_embedding_window_to_the_code_owned_maximum(int bound, bool valid) =>
        Assert.Equal(valid, LongRestDiscoveryHash.Recipe(Recipe(bound)).IsSuccess);

    // Independently assembled preimages (Python struct.pack and hashlib.sha256 over the recipe fields in their canonical order, with the summary
    // bound as the third Int32BE of the bounds block). The same script reproduces the four existing 8,192-byte goldens in
    // LongRestDiscoveryPolicyTests (CF9B0D84... at 32768, 7BDFE21E... at 4096, 226A14F8... at 5328, 9264684C... at 20000), so only the bound
    // moved the rows below. 7109 at 5328 is the recipe the default 8,192-token window resolves; 1024 is the smallest bound a recipe may carry
    // (the digest does not encode the minimum, so the 2,048 row is unchanged by it); 1766 is the bound of a 2,048-token embedding window.
    [Theory]
    [InlineData(1024, 32768, "44EBB675073E16C2FCD5B380D94B0C877D11D8E465940D8C724ED1F97F5BB969")]
    [InlineData(1024, 5328, "1AE81E71B6874A3CA11283C35B14CA2DB5CB6438DE3EC77DA044596B1FA6942D")]
    [InlineData(1766, 32768, "387DB9D8EDBDD7C384B02C8C4A70C93C3933FC64681C7941E6A47DAE8B6ABFA5")]
    [InlineData(1766, 5328, "E187FB3174EDF42B556946272C56B732D0EC535DE35B1805CACDBE4AF435DE99")]
    [InlineData(2048, 32768, "8B329D584F3415C54B327F27935626DB3B71A7DF2D827EBB75AA44A296E8F57E")]
    [InlineData(7109, 32768, "DF871C156F94D4D2AF889B0B10049307E0EDC4E26C61814CFDAD7D7DA5B87151")]
    [InlineData(7109, 5328, "4542ECCE136C9BD996515E21F68598593FFB54175813A4A3179D1679F8606DE0")]
    [InlineData(8191, 32768, "96C8CC2954F8490E20F9331F03880295DE1A469B394DD1310E69777022AD0464")]
    [InlineData(8192, 32768, "CF9B0D84FBD2D8B8CB823EE814682AC04DAF9F4CE8E93EE72564CDDEAA7AB054")]
    [InlineData(8192, 5328, "226A14F8FE41E3E1DE3A8C777D52B75C2BB9C64552C02E2DCEAA7FE75B4D4A37")]
    public void Recipe_digest_golden_vectors_pin_the_summary_bound(int bound, int budget, string expected) =>
        Assert.Equal(expected, LongRestDiscoveryHash.Recipe(Recipe(bound, budget)).Value.ToString());

    [Fact]
    public void Every_distinct_summary_bound_is_a_distinct_recipe_digest()
    {
        HashSet<string> digests = [];

        for (int bound = LongRestPromptLayout.MinimumSummaryUtf8Bytes; bound <= LongRestDiscoveryLimits.SummaryUtf8Bytes; bound += 7)
        {
            Assert.True(digests.Add(LongRestDiscoveryHash.Recipe(Recipe(bound)).Value.ToString()), $"bound {bound}");
        }
    }

    private static ArcanumSettings Settings(int embeddingWindow)
    {
        ArcanumSettings settings = LongRestConfigurationEpochTests.Settings();

        settings.Providers =
        [
            settings.Providers[0],
            new() { Name = "embedder", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://embedder.test/v1", Models = ["embedding"], ContextWindowLimit = embeddingWindow },
        ];

        settings.Integrations.Embeddings.Provider = "embedder";

        return settings;
    }

    [Theory]
    [InlineData(1194, 1024)]
    [InlineData(2048, 1766)]
    [InlineData(2372, 2048)]
    [InlineData(4096, 3547)]
    [InlineData(8192, 7109)]
    [InlineData(9436, 8191)]
    [InlineData(9437, 8192)]
    [InlineData(131072, 8192)]
    [InlineData(0, 8192)]
    public void The_resolved_recipe_binds_the_bound_its_embedding_provider_window_allows(int embeddingWindow, int bound)
    {
        LongRestRecipeIdentity recipe = LongRestRecipeResolver.Resolve(Settings(embeddingWindow)).Value;

        Assert.Equal(bound, recipe.SummaryUtf8Bytes);

        Assert.True(LongRestDiscoveryHash.Recipe(recipe).IsSuccess);
    }

    [Fact]
    public void A_change_of_the_embedding_window_that_moves_the_bound_is_a_new_recipe_and_one_that_does_not_is_not()
    {
        LongRestRecipeIdentity small = LongRestRecipeResolver.Resolve(Settings(8192)).Value;

        LongRestRecipeIdentity large = LongRestRecipeResolver.Resolve(Settings(16384)).Value;

        LongRestRecipeIdentity larger = LongRestRecipeResolver.Resolve(Settings(32768)).Value;

        Assert.NotEqual(LongRestDiscoveryHash.Recipe(small).Value, LongRestDiscoveryHash.Recipe(large).Value);

        Assert.Equal(LongRestDiscoveryHash.Recipe(large).Value, LongRestDiscoveryHash.Recipe(larger).Value);

        // Below the smallest embedding window the recipe is still unresolved; no bound is resolved for it.
        Assert.Equal(ErrorCodes.LongRest.ModelContextTooSmall, LongRestRecipeResolver.Resolve(Settings(LongRestPromptLayout.MinimumEmbeddingWindowTokens - 1)).Error.Code);
    }

    // The common local embedding models report a 2,048-token window (Ollama's embeddinggemma has a context_length of 2048). The chat answer
    // reservation no longer sets the floor, because the summary request states a bound the window holds, so such a model is a legal embedding provider.
    [Fact]
    public void A_2048_token_embedding_window_resolves_with_its_own_bound_and_every_summary_within_it_passes_the_embedding_estimator_and_the_window_gate()
    {
        const int window = 2048;

        int bound = LongRestPromptLayout.SummaryBoundUtf8Bytes(window);

        Assert.Equal(1766, bound);

        LongRestRecipeIdentity recipe = LongRestRecipeResolver.Resolve(Settings(window)).Value;

        Assert.Equal(bound, recipe.SummaryUtf8Bytes);

        Assert.True(LongRestDiscoveryHash.Recipe(recipe).IsSuccess);

        Assert.Empty(LongRestRecipeResolver.Problems(Settings(window)));

        // The request the model receives states that bound.
        LongRestChatRequest request = LongRestProviderCallFreezer.RenderChat(
            LongRestPackingData.SummaryPayload(TwoChildren(), LongRestPackingData.Digest, recipe.ProviderInputUtf8Bytes, recipe.SummaryUtf8Bytes).Value).Value;

        Assert.Contains("must not exceed 1766 bytes", request.SystemPrompt, StringComparison.Ordinal);

        foreach (bool catalog in new[] { false, true })
        {
            ProviderSettings provider = EmbeddingProvider(window, catalog);

            string model = catalog ? "gpt-4o" : "embedding";

            for (int size = 1; size <= bound; size++)
            {
                foreach (string text in new[] { new string('a', size), LongRestPackingData.Content(new Random(size), size) })
                {
                    Assert.Equal(size, new UTF8Encoding(false, true).GetByteCount(text));

                    int tokens = Estimator.EstimateLongRestEmbedding(provider, model, EmbeddingPayload(text)).TokenCount;

                    Assert.True(tokens + LongRestPromptLayout.FramingTokens <= window, $"catalog {catalog}, {size} bytes: {tokens} tokens plus framing");

                    Assert.True(ModelCallExecutor.CheckLongRestWindow(new LongRestProviderAdmission.Embedding(provider, model, 64, tokens, Zero)).IsSuccess, $"catalog {catalog}, {size} bytes");
                }
            }
        }
    }

    [Fact]
    public void A_window_one_token_below_the_smallest_is_refused_and_the_reason_names_the_new_minimum()
    {
        ArcanumSettings settings = Settings(LongRestPromptLayout.MinimumEmbeddingWindowTokens - 1);

        Assert.Equal(ErrorCodes.LongRest.ModelContextTooSmall, LongRestRecipeResolver.Resolve(settings).Error.Code);

        string problem = Assert.Single(LongRestRecipeResolver.Problems(settings));

        Assert.StartsWith("the embedding provider 'embedder' has a contextWindowLimit of 1193 tokens, below the 1194", problem, StringComparison.Ordinal);

        Assert.DoesNotContain("2372", problem, StringComparison.Ordinal);

        // The smallest window itself resolves, with the smallest bound.
        Assert.Equal(LongRestPromptLayout.MinimumSummaryUtf8Bytes, LongRestRecipeResolver.Resolve(Settings(LongRestPromptLayout.MinimumEmbeddingWindowTokens)).Value.SummaryUtf8Bytes);

        Assert.Empty(LongRestRecipeResolver.Problems(Settings(LongRestPromptLayout.MinimumEmbeddingWindowTokens)));
    }

    private static string Summary(int bytes) => "{\"summary\":\"" + new string('a', bytes) + "\"}";

    private static Func<Task> Respond(LongRestMaintenanceTests.MaintenanceFixture f, List<string> bodies, int summaryBytes) => () =>
    {
        bodies.Add(f.Handler.LastBody!);

        f.Handler.ChatText = f.Handler.LastBody!.Contains("long_rest_summary", StringComparison.Ordinal)
            ? Summary(summaryBytes) : "{\"kind\":\"NoChange\",\"survivorVersionId\":null}";

        return Task.CompletedTask;
    };

    private static Task<LongRestMaintenanceTests.MaintenanceFixture> DefaultWindowFixture() =>
        LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false,
            configure: static settings => settings.Providers[0].ContextWindowLimit = 8192);

    [Fact]
    public async Task The_real_summary_request_states_the_bound_and_the_fixed_cap_and_a_bound_sized_answer_completes_the_sweep()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await DefaultWindowFixture();

        // At the default 8,192-token window the bound is 7,109 bytes; the cap is the fixed 2,048-token answer reservation.
        LongRestRecipeIdentity recipe = ((ILongRestConfigurationProvider)f.Epoch).Capture().Recipe.Value;

        Assert.Equal(7109, recipe.SummaryUtf8Bytes);

        List<string> bodies = [];

        f.Handler.InsideSend = Respond(f, bodies, 7109);

        using AsyncFlowMetricCapture metrics = new(RefusedMetric);

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, progress.Phase);

        Assert.Equal(0, metrics.Sum(RefusedMetric));

        string summary = Assert.Single(bodies, static body => body.Contains("long_rest_summary", StringComparison.Ordinal));

        Assert.Contains("must not exceed 7109 bytes", summary, StringComparison.Ordinal);

        Assert.Matches(new Regex("\"max_(completion_)?tokens\"\\s*:\\s*2048\\b"), summary);

        // The summary was embedded: the bound-sized answer fit the window the bound came from.
        Assert.Contains(bodies, static body => body.Contains("\"input\":", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_model_that_ignores_the_bound_is_paid_for_refused_by_the_embedding_gate_and_counted()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await DefaultWindowFixture();

        List<string> bodies = [];

        // 7,500 bytes is a valid answer by the code-owned maximum, but above the 7,109 the recipe states: charged at 8,625 embedding tokens.
        f.Handler.InsideSend = Respond(f, bodies, 7500);

        using AsyncFlowMetricCapture metrics = new(RefusedMetric);

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Assert.Equal(ErrorCodes.LongRest.ModelContextTooSmall, progress.DeferredReason);

        Assert.Equal(1, metrics.Sum(RefusedMetric));

        // The last guard held before anything was sent for the embedding: one chat call, no embedding request.
        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await f.Operations.GetAsync(f.Operation))!.State);

        Assert.Equal(ErrorCodes.LongRest.ModelContextTooSmall, (await f.Operations.GetAsync(f.Operation))!.TerminalErrorCode);
    }
}
