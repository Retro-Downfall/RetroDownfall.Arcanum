using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>One transcript bubble, status notice, or tool card host in The Tome.</summary>
public sealed partial class ChatMessageViewModel : ObservableObject
{
    public const int DefaultMaxReasoningChars = 64 * 1024;

    public const int DefaultMaxContentChars = 200_000;

    public const string ReasoningTruncationMarker = "\n… [reasoning truncated]";

    public const string ContentTruncationMarker = "\n… [content truncated]";

    private const int PublishChunkThreshold = 32;

    private readonly int _maxContentChars;

    private readonly string _truncationMarker;

    private StringBuilder? _contentBuilder;

    private string _content = string.Empty;

    private int _pendingContentChunks;

    private bool _contentTruncated;

    [ObservableProperty]
    private bool _isPinned;

    public ChatMessageViewModel(
        string role,
        string content,
        ToolCallCardViewModel? toolCall = null,
        Guid? entryId = null,
        bool isPinned = false)
    {

        Role = role;

        bool reasoning = string.Equals(role, "reasoning", StringComparison.OrdinalIgnoreCase);
        _maxContentChars = reasoning ? DefaultMaxReasoningChars : DefaultMaxContentChars;
        _truncationMarker = reasoning ? ReasoningTruncationMarker : ContentTruncationMarker;
        _content = content ?? string.Empty;
        if (_content.Length > _maxContentChars)
        {
            int contentLimit = _maxContentChars - _truncationMarker.Length;
            _content = _content[..Utf8Truncation.SafeCharSliceLength(_content, contentLimit)] + _truncationMarker;
            _contentTruncated = true;
        }

        ToolCall = toolCall;

        EntryId = entryId;

        IsPinned = isPinned;

    }

    public string Role { get; }

    public string Content
    {
        get => _content;
        private set => SetProperty(ref _content, value);
    }

    private Guid? _entryId;

    public Guid? EntryId
    {

        get => _entryId;

        set
        {

            if (_entryId == value)
            {

                return;

            }

            _entryId = value;

            OnPropertyChanged();

            OnPropertyChanged(nameof(HasEntryId));

            OnPropertyChanged(nameof(CanPin));

            OnPropertyChanged(nameof(CanUnpin));

        }

    }

    public ToolCallCardViewModel? ToolCall { get; }

    public bool IsUser => string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase);

    public bool IsAssistant => string.Equals(Role, "assistant", StringComparison.OrdinalIgnoreCase);

    public bool IsReasoning => string.Equals(Role, "reasoning", StringComparison.OrdinalIgnoreCase);

    public bool IsSystem => string.Equals(Role, "system", StringComparison.OrdinalIgnoreCase);

    public bool IsTool => string.Equals(Role, "tool", StringComparison.OrdinalIgnoreCase) || ToolCall is not null;

    public bool IsStatus => string.Equals(Role, "status", StringComparison.OrdinalIgnoreCase);

    public bool IsError => string.Equals(Role, "error", StringComparison.OrdinalIgnoreCase);

    public bool HasEntryId => EntryId.HasValue;

    public bool CanPin => HasEntryId && !IsPinned;

    public bool CanUnpin => HasEntryId && IsPinned;

    public void AppendContent(string data)
    {
        if (!AppendToBuffer(data))
        {
            return;
        }

        _pendingContentChunks++;
        if (_contentTruncated
            || _pendingContentChunks >= PublishChunkThreshold
            || data.Contains('\n', StringComparison.Ordinal))
        {
            PublishPendingContent();
        }
    }

    public void PublishPendingContent()
    {
        if (_pendingContentChunks == 0)
        {
            return;
        }

        Content = _contentBuilder!.ToString();
        _pendingContentChunks = 0;
    }

    public void CompleteStreamingContent()
    {
        PublishPendingContent();
        _contentBuilder = null;
    }

    private bool AppendToBuffer(string? data)
    {
        if (_contentTruncated || string.IsNullOrEmpty(data))
        {
            return false;
        }

        StringBuilder contentBuilder = _contentBuilder ??= new StringBuilder(_content);
        if (contentBuilder.Length + data.Length <= _maxContentChars)
        {
            _ = contentBuilder.Append(data);
            return true;
        }

        int contentLimit = _maxContentChars - _truncationMarker.Length;
        if (contentBuilder.Length > contentLimit)
        {
            contentBuilder.Length = contentLimit;
        }

        int available = contentLimit - contentBuilder.Length;
        if (available > 0)
        {
            _ = contentBuilder.Append(data.AsSpan(0, Utf8Truncation.SafeCharSliceLength(data, available)));
        }

        // Both cuts above land on a raw UTF-16 code unit, which can fall between the halves of a
        // surrogate pair. Drop an orphaned high surrogate so the astral-plane glyph is dropped whole
        // rather than rendering as a replacement character before the marker (DESIGN §16.7).
        if (contentBuilder.Length > 0 && char.IsHighSurrogate(contentBuilder[^1]))
        {
            contentBuilder.Length--;
        }

        _ = contentBuilder.Append(_truncationMarker);
        _contentTruncated = true;
        return true;
    }

    partial void OnIsPinnedChanged(bool value)
    {

        OnPropertyChanged(nameof(CanPin));

        OnPropertyChanged(nameof(CanUnpin));

    }

}
