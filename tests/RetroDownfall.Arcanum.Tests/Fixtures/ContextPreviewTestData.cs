using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

internal static class ContextPreviewTestData
{

    public static ContextPreviewResult Create(bool showContent = false) =>

        new(

            "test-provider",

            "test-model",

            128_000,

            "test-spell",

            "directResonance",

            "Production routing selected the test Spell.",

            ["dependency"],

            [new ContextPreviewTool("read_file", "mcp", true, "advertised")],

            [new ContextPreviewSource(

                ContextTokenSource.SystemCodexSpell,

                42,

                TokenEstimateClassification.Estimated,

                true,

                "assembled",

                showContent ? "secret system prompt" : null)],

            new ContextPreviewCompression(false, 100_000, "below threshold"),

            [],

            new ContextPreviewTokenSummary(

                100,

                120,

                20,

                TokenEstimateClassification.Estimated,

                "test-profile"),

            showContent

                ? new ContextPreviewContent("secret system prompt", [])

                : null);

}
