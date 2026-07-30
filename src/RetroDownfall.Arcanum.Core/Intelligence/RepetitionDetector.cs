using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Core.Intelligence;

public sealed record ToolCallSignature(
    string ToolName,
    string ArgumentsHash,
    string ResultHash);

public sealed record RoundSignature(
    int RoundIndex,
    int ToolCallCount,
    string? TextHash,
    IReadOnlyList<ToolCallSignature> ToolCalls);

public sealed record PatchCycleSignature(
    string PatchHash,
    string ErrorHash,
    int AttemptIndex);

public enum RepetitionVerdict
{
    None = 0,
    IdenticalToolCall = 1,
    MateriallyEquivalentToolCall = 2,
    FailedPatchCycle = 3,
    NoProgressRound = 4,
}

public sealed class RepetitionDetector
{
    private readonly int _maxIdenticalToolCalls;
    private readonly int _maxFailedPatchCycles;
    private readonly int _maxNoProgressRounds;
    private readonly double _similarityThreshold;

    private readonly List<ToolCallSignature> _recentToolCalls = new();
    private readonly List<RoundSignature> _recentRounds = new();

    public RepetitionDetector(
        int maxIdenticalToolCalls = 3,
        int maxFailedPatchCycles = 2,
        int maxNoProgressRounds = 2,
        double similarityThreshold = 0.92)
    {
        _maxIdenticalToolCalls = maxIdenticalToolCalls;
        _maxFailedPatchCycles = maxFailedPatchCycles;
        _maxNoProgressRounds = maxNoProgressRounds;
        _similarityThreshold = similarityThreshold;
    }

    public RepetitionVerdict AnalyzeToolCall(string toolName, string argumentsJson, string resultText)
    {
        var argsHash = ComputeHash(argumentsJson);
        var resultHash = ComputeHash(resultText);

        var signature = new ToolCallSignature(toolName, argsHash, resultHash);
        var identicalCount = 0;
        var equivalentCount = 0;

        foreach (var recent in _recentToolCalls)
        {
            if (recent.ToolName == signature.ToolName &&
                recent.ArgumentsHash == signature.ArgumentsHash &&
                recent.ResultHash == signature.ResultHash)
            {
                identicalCount++;
            }
            else if (recent.ToolName == signature.ToolName &&
                     Similarity(recent.ArgumentsHash, signature.ArgumentsHash) > _similarityThreshold &&
                     Similarity(recent.ResultHash, signature.ResultHash) > _similarityThreshold)
            {
                equivalentCount++;
            }
        }

        _recentToolCalls.Add(signature);

        if (identicalCount >= _maxIdenticalToolCalls - 1)
            return RepetitionVerdict.IdenticalToolCall;

        if (equivalentCount >= _maxIdenticalToolCalls - 1)
            return RepetitionVerdict.MateriallyEquivalentToolCall;

        return RepetitionVerdict.None;
    }

    public RepetitionVerdict AnalyzeRound(int roundIndex, int toolCallCount, string? textHash, IReadOnlyList<ToolCallSignature> toolCalls)
    {
        var round = new RoundSignature(roundIndex, toolCallCount, textHash, toolCalls);
        _recentRounds.Add(round);

        // Window includes the round just recorded: the verdict fires when the trailing
        // _maxNoProgressRounds rounds (current included) all show no new evidence.
        int noProgressCount = 0;
        for (int i = Math.Max(0, _recentRounds.Count - _maxNoProgressRounds); i < _recentRounds.Count; i++)
        {
            var prev = _recentRounds[i];
            if (prev.ToolCallCount == 0 && (prev.TextHash == null || prev.TextHash == textHash))
            {
                noProgressCount++;
            }
        }

        if (noProgressCount >= _maxNoProgressRounds)
            return RepetitionVerdict.NoProgressRound;

        return RepetitionVerdict.None;
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16];
    }

    private static double Similarity(string a, string b)
    {
        if (a == b) return 1.0;
        int distance = ComputeLevenshteinDistance(a, b);
        int maxLength = Math.Max(a.Length, b.Length);
        return maxLength == 0 ? 1.0 : 1.0 - (double)distance / maxLength;
    }

    private static int ComputeLevenshteinDistance(string s, string t)
    {
        int[,] d = new int[s.Length + 1, t.Length + 1];
        for (int i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= t.Length; j++) d[0, j] = j;
        for (int i = 1; i <= s.Length; i++)
            for (int j = 1; j <= t.Length; j++)
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + (s[i - 1] == t[j - 1] ? 0 : 1));
        return d[s.Length, t.Length];
    }
}
