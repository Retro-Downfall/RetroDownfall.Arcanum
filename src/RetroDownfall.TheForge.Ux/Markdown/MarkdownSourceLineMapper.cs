namespace RetroDownfall.TheForge.Ux.Markdown;

/// <summary>Maps source editor lines to nearest rendered markdown block.</summary>
public sealed class MarkdownSourceLineMapper
{

    private readonly IReadOnlyList<MarkdownSourceBlockAnchor> _anchors;

    public MarkdownSourceLineMapper(IEnumerable<MarkdownSourceBlockAnchor> anchors)
    {

        _anchors = anchors
            .Where(static a => a.SourceLine >= 0)
            .OrderBy(static a => a.SourceLine)
            .ToArray();

    }

    public MarkdownSourceBlockAnchor? FindNearest(int sourceLine)
    {

        if (_anchors.Count == 0)
        {

            return null;

        }

        if (sourceLine <= _anchors[0].SourceLine)
        {

            return _anchors[0];

        }

        MarkdownSourceBlockAnchor? best = _anchors[0];

        foreach (MarkdownSourceBlockAnchor anchor in _anchors)
        {

            if (anchor.SourceLine <= sourceLine)
            {

                best = anchor;

            }
            else
            {

                break;

            }

        }

        return best;

    }

}

public sealed record MarkdownSourceBlockAnchor(int SourceLine, string BlockId);
