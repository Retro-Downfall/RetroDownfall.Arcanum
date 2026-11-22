using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Turn-local evidence of earlier file operations whose original tool result was trimmed.</summary>
internal sealed class TrimmedWorkspaceMutations
{
    private const string Heading = "Earlier native file changes. Check the workspace for their current state.";

    private readonly ConditionalWeakTable<ChatMessage, IReadOnlyList<WorkspaceMutationFact>> _resultFacts = new();

    private readonly LinkedList<Change> _recent = new();

    private readonly Dictionary<string, LinkedListNode<Change>> _byPath = new(StringComparer.Ordinal);

    private TextContent? _projection;

    internal void Associate(ChatMessage result, IReadOnlyList<WorkspaceMutationFact>? facts)
    {
        if (facts is { Count: > 0 }) _resultFacts.Add(result, facts);
    }

    internal void ObserveEviction(ChatMessage message)
    {
        if (!_resultFacts.TryGetValue(message, out var facts)) return;

        _resultFacts.Remove(message);

        foreach (WorkspaceMutationFact fact in facts)
        {
            if (_byPath.Remove(fact.RelativePath, out var previous)) _recent.Remove(previous);

            _byPath.Add(fact.RelativePath, _recent.AddFirst(new Change(fact)));
        }
    }

    internal void RemoveProjection(List<ChatMessage> messages)
    {
        if (_projection is null) return;

        messages.Find(message => message.Contents.Contains(_projection))?.Contents.Remove(_projection);

        _projection = null;
    }

    internal bool TryProject(List<ChatMessage> messages, TextContent notice, int window, int available, Func<string, int> estimateText)
    {
        if (_recent.Count == 0) return false;

        ChatMessage? owner = messages.Find(message => message.Contents.Contains(notice));

        StringBuilder text = new(Heading);

        // Keep only as much historical metadata as this provider's window can represent.
        // The current request can have much less room. Its unused space can grow on a later call.
        int displayRoom = Math.Max(0, available - estimateText(Heading));

        for (var node = _recent.First; node is not null;)
        {
            var next = node.Next;

            Change change = node.Value;

            int cost = change.EstimatedTokens ??= Math.Max(1, estimateText(change.Line));

            if (cost > window)
            {
                _byPath.Remove(change.Path);
                _recent.Remove(node);
            }
            else
            {
                window -= cost;

                if (cost <= displayRoom)
                {
                    text.Append(change.Line);
                    displayRoom -= cost;
                }
            }

            node = next;
        }

        if (owner is null || text.Length == Heading.Length) return false;

        _projection = new TextContent(text.ToString());
        owner.Contents.Add(_projection);

        return true;
    }

    private sealed class Change(WorkspaceMutationFact fact)
    {
        internal string Path => fact.RelativePath;

        // A legal filename can contain quotes or markup. Keep it a single data value.
        internal string Line { get; } = $"\n{(fact.Operation == WorkspaceMutationOperation.Rename ? "Rename to" : fact.Operation.ToString())} \"{JsonEncodedText.Encode(fact.RelativePath)}\"";

        internal int? EstimatedTokens { get; set; }
    }
}
