namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

/// <summary>
/// Resolves how many recent session entries to load for each repository read path.
/// </summary>
/// <remarks>
/// <para>
/// Four sibling methods historically computed their take/window inline with different rules.
/// Each call site passes a named <see cref="EntryWindowKind"/> so the behavior stays explicit
/// while the math lives in one place.
/// </para>
/// <list type="bullet">
/// <item>
/// <see cref="EntryWindowKind.WatermarkAware"/> — <c>GetSessionAsync</c>: start at
/// <paramref name="maxMessages"/>; when the session has a summary watermark, expand to
/// <c>Math.Max(maxMessages, afterWatermarkCount)</c> so every post-watermark entry is loaded
/// (can exceed <paramref name="maxMessages"/>).
/// </item>
/// <item>
/// <see cref="EntryWindowKind.MaxMessagesOnly"/> — <c>GetSessionEntriesAsync</c>: always
/// <paramref name="maxMessages"/>; ignores watermark and <paramref name="requestedTake"/>.
/// </item>
/// <item>
/// <see cref="EntryWindowKind.ClampedTakeLast"/> — <c>GetRecentSessionEntriesAsync</c>:
/// <c>Math.Clamp(requestedTake, 1, maxMessages)</c>; no watermark.
/// </item>
/// <item>
/// <see cref="EntryWindowKind.RawTakeLast"/> — <c>GetEntriesAscendingAsync</c> (Forge SSE replay):
/// <c>Math.Max(1, requestedTake)</c>; no <paramref name="maxMessages"/> clamp and no watermark.
/// </item>
/// </list>
/// <para>
/// <b>Open product question:</b> Should Forge replay (<c>GetEntriesAscendingAsync</c>) honor
/// watermark expansion and the <paramref name="maxMessages"/> clamp? Today it does neither.
/// </para>
/// </remarks>
internal static class EntryWindowPolicy
{

    internal enum EntryWindowKind
    {

        WatermarkAware,

        MaxMessagesOnly,

        ClampedTakeLast,

        RawTakeLast,

    }

    /// <summary>
    /// Computes the take count for loading recent entries under the given policy.
    /// </summary>
    /// <param name="kind">Which repository read path's rules to apply.</param>
    /// <param name="maxMessages">
    /// Clamped <c>MaxMessagesPerConversationLoad</c> setting. Unused for
    /// <see cref="EntryWindowKind.RawTakeLast"/>.
    /// </param>
    /// <param name="requestedTake">Caller-supplied take (e.g. <c>takeLast</c>). Unused for
    /// <see cref="EntryWindowKind.WatermarkAware"/> and <see cref="EntryWindowKind.MaxMessagesOnly"/>.
    /// </param>
    /// <param name="hasWatermark">
    /// Whether the session has <c>LastSummarizedMessageAt</c>. Only consulted for
    /// <see cref="EntryWindowKind.WatermarkAware"/>.
    /// </param>
    /// <param name="afterWatermarkCount">
    /// Count of entries strictly after the summary watermark. Only consulted when
    /// <paramref name="hasWatermark"/> is <see langword="true"/> for
    /// <see cref="EntryWindowKind.WatermarkAware"/>.
    /// </param>
    internal static int ResolveTake(
        EntryWindowKind kind,
        int maxMessages,
        int requestedTake = 0,
        bool hasWatermark = false,
        int afterWatermarkCount = 0)
    {

        return kind switch
        {

            EntryWindowKind.WatermarkAware when hasWatermark =>
                Math.Max(maxMessages, afterWatermarkCount),

            EntryWindowKind.WatermarkAware =>
                maxMessages,

            EntryWindowKind.MaxMessagesOnly =>
                maxMessages,

            EntryWindowKind.ClampedTakeLast =>
                Math.Clamp(requestedTake, 1, maxMessages),

            EntryWindowKind.RawTakeLast =>
                Math.Max(1, requestedTake),

            _ =>
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null),

        };

    }

}
