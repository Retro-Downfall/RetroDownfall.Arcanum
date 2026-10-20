namespace RetroDownfall.Arcanum.PromptCache.Benchmarks;

public sealed record PromptCacheBenchmarkArguments(bool Fixture, bool Smoke, string? Endpoint, string ColdStart, string Record, string SourceRoot, int? Pairs, int? Samples, string Model = "gemma4:e4b")
{
    public static bool TryParse(string[] args, out PromptCacheBenchmarkArguments? parsed)
    {
        parsed = null;

        Dictionary<string, string> values = new(StringComparer.Ordinal);

        bool fixture = false;

        bool smoke = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--fixture" or "--smoke")
            {
                if (fixture) return false;

                fixture = true;
                smoke = args[i] == "--smoke";
                continue;
            }

            if (args[i] is not ("--ollama-endpoint" or "--model" or "--cold-start" or "--record" or "--source-root" or "--pairs" or "--samples")
                || i + 1 >= args.Length || !values.TryAdd(args[i], args[++i])) return false;
        }

        values.TryGetValue("--ollama-endpoint", out string? endpoint);

        string model = values.GetValueOrDefault("--model", "gemma4:e4b");

        if (fixture == (endpoint is not null) || !values.TryGetValue("--record", out string? record) || string.IsNullOrWhiteSpace(record)
            || !values.TryGetValue("--source-root", out string? source) || string.IsNullOrWhiteSpace(source)
            || !PromptCacheBenchmarkEvidence.IsSelectedModel(model)) return false;

        if (endpoint is not null && (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || uri.Scheme != "http"
            || uri.Host != "127.0.0.1" || uri.Port != 11434 || uri.AbsolutePath.TrimEnd('/') != "/v1"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)) return false;

        string cold = values.GetValueOrDefault("--cold-start", "observed");

        if (cold is not ("reload" or "observed") || fixture && values.ContainsKey("--cold-start")) return false;

        int? pairs = null;

        int? samples = null;

        if (values.TryGetValue("--pairs", out string? pairText))
        {
            if (!int.TryParse(pairText, out int number) || number is < 2 or > 12) return false;

            pairs = number;
        }

        if (values.TryGetValue("--samples", out string? sampleText))
        {
            if (!int.TryParse(sampleText, out int number) || number is < 1 or > 10) return false;

            samples = number;
        }

        parsed = new(fixture, smoke, endpoint?.TrimEnd('/'), cold, record, source, pairs, samples, model);

        return true;
    }
}
