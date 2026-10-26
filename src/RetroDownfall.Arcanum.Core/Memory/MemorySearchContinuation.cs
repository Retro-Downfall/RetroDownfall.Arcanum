using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Memory;

/// <summary>Why a read-only search page stopped before proving exhaustion.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemorySearchTruncationReason>))]
public enum MemorySearchTruncationReason
{
    ResultLimit,
    ContentBytes,
    ExaminedRows,
    ExaminedBytes,
    StatementLimit,
}

/// <summary>Native/Saga stored order coordinates; Lexicon uses only ID/scope and resolves its name under current authority.</summary>
public sealed record MemorySearchPosition(int Phase, string? Time = null, long Number = 0, string? Path = null, string? Id = null, string? Tier = null);

/// <summary>A versioned cursor bound to selectors and effective inspection policy.</summary>
public sealed record MemorySearchCursor(int Version, string Binding, MemorySearchPosition Position);

/// <summary>One authoritative native hit and the coordinate strictly following it.</summary>
public sealed record NativeMemorySearchHit(MemorySearchResultDto Result, MemorySearchPosition Position);

public sealed record NativeMemorySearchPage(NativeMemorySearchHit[] Hits, MemorySearchPosition Position, MemorySearchTruncationReason? Reason);
