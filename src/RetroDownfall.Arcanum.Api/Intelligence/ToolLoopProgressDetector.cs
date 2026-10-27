using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using System.Text;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal readonly record struct ToolLoopProgressEntry(
    string ToolName,
    string ArgumentsJson,
    string ResultText);

/// <summary>
/// Detects repetition in the last eight completed tool rounds. An exact host-owned message witness
/// permits reacquiring evidence lost to context trimming once while its fingerprint is retained;
/// another identical eviction/reacquisition cycle remains no progress. The fixed ring owns only
/// fingerprints/flags; weak message associations retain neither removed payload nor authority.
/// Productive loops still have no total round, duration or model-call ceiling (DESIGN §2.1).
/// </summary>
internal sealed class ToolLoopProgressDetector
{
    /// <summary>
    /// How many completed rounds the current round is compared against. Small and fixed so memory
    /// stays constant; wide enough to catch the two- and three-round oscillations models fall into.
    /// </summary>
    private const int RecentRoundWindow = 8;

    private readonly RoundEvidence?[] _recentRounds = new RoundEvidence?[RecentRoundWindow];

    private readonly ConditionalWeakTable<ChatMessage, RoundEvidence> _messageEvidence = new();

    private readonly TextContent _trimNotice = new("Tool evidence trimmed; reread narrowly.");

    private int _nextSlot;

    /// <summary>
    /// Hex digest of the round signature that most recently recurred, for trace/diagnostic capture
    /// of the <c>no_progress</c> stop (DESIGN §10.7.3). <see langword="null"/> until one recurs.
    /// </summary>
    public string? RepeatedSignature { get; private set; }

    private byte[]? _protectedKey;

    /// <summary>Preserves equality within this turn without retaining protected text or a stable digest.</summary>
    public ToolLoopProgressEntry CreateEntry(string toolName, string argumentsJson, string resultText, bool protect)
    {
        if (!protect)
        {
            return new(toolName, argumentsJson, resultText);
        }

        _protectedKey ??= RandomNumberGenerator.GetBytes(32);

        return new(toolName, Protect(argumentsJson), Protect(resultText));
    }

    private string Protect(string content) =>
        Convert.ToHexString(HMACSHA256.HashData(_protectedKey!, Encoding.UTF8.GetBytes(content)));

    public bool ObserveCompletedRound(
        IReadOnlyList<ToolLoopProgressEntry> entries,
        IReadOnlyList<ChatMessage>? exchangeMessages = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            throw new ArgumentException(
                "A completed tool round must contain at least one entry.",
                nameof(entries));
        }

        byte[] fingerprint = ComputeFingerprint(entries);

        bool repeated = false;
        bool allEvidenceEvicted = true;
        bool reacquisitionUsed = false;

        foreach (RoundEvidence? previous in _recentRounds)
        {
            if (previous is not null && CryptographicOperations.FixedTimeEquals(previous.Fingerprint, fingerprint))
            {
                repeated = true;
                allEvidenceEvicted &= previous.EvidenceEvicted;
                reacquisitionUsed |= previous.ReacquisitionUsed;
            }
        }

        // A visible equivalent result is still evidence. Repeated loss never renews the allowance,
        // including when several retained observations have this same fingerprint.
        bool reacquiring = repeated && allEvidenceEvicted && !reacquisitionUsed;

        if (reacquiring)
        {
            foreach (RoundEvidence? previous in _recentRounds)
            {
                if (previous is not null && CryptographicOperations.FixedTimeEquals(previous.Fingerprint, fingerprint))
                    previous.ReacquisitionUsed = true;
            }
        }

        RoundEvidence current = new(fingerprint) { ReacquisitionUsed = reacquisitionUsed || reacquiring };
        _recentRounds[_nextSlot] = current;
        _nextSlot = (_nextSlot + 1) % RecentRoundWindow;

        if (exchangeMessages is not null)
        {
            foreach (ChatMessage message in exchangeMessages)
            {
                if (message.Contents.Any(static content => content is FunctionCallContent or FunctionResultContent))
                    _messageEvidence.AddOrUpdate(message, current);
            }
        }

        bool stopped = repeated && !reacquiring;

        if (stopped && _protectedKey is null)
            RepeatedSignature = Convert.ToHexString(fingerprint.AsSpan(0, 8));

        return stopped;
    }

    internal ChatMessage? EnsureTrimNotice(List<ChatMessage> messages)
    {
        if (messages.Any(message => message.Contents.Any(content => ReferenceEquals(content, _trimNotice)))) return null;
        ChatMessage? user = messages.LastOrDefault(static message => message.Role == ChatRole.User);
        if (user is null) messages.Add(new ChatMessage(ChatRole.Assistant, [_trimNotice]));
        else user.Contents.Add(_trimNotice);
        return new ChatMessage(ChatRole.User, [_trimNotice]);
    }

    internal void ObserveTrimmedExchanges(IReadOnlyList<ChatMessage> removed)
    {
        foreach (ChatMessage message in removed)
        {
            if (_messageEvidence.TryGetValue(message, out RoundEvidence? evidence)) evidence.EvidenceEvicted = true;
        }
    }

    private sealed class RoundEvidence(byte[] fingerprint)
    {
        internal byte[] Fingerprint { get; } = fingerprint;
        internal bool EvidenceEvicted { get; set; }
        internal bool ReacquisitionUsed { get; set; }
    }

    private static byte[] ComputeFingerprint(
        IReadOnlyList<ToolLoopProgressEntry> entries)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);

        AppendInt32(hash, entries.Count);

        foreach (ToolLoopProgressEntry entry in entries)
        {
            AppendString(hash, entry.ToolName);

            AppendString(hash, entry.ArgumentsJson);

            AppendString(hash, entry.ResultText);
        }

        return hash.GetHashAndReset();
    }

    private static void AppendString(
        IncrementalHash hash,
        string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);

        AppendInt32(hash, bytes.Length);

        hash.AppendData(bytes);
    }

    private static void AppendInt32(
        IncrementalHash hash,
        int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];

        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);

        hash.AppendData(bytes);
    }
}
