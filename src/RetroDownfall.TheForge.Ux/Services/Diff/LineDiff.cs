namespace RetroDownfall.TheForge.Ux.Services.Diff;

public enum LineDiffKind
{
    Unchanged,
    Added,
    Removed,
}

public sealed record DiffLineItem(
    LineDiffKind Kind,
    string Text,
    int? LeftLineNumber,
    int? RightLineNumber)
{

    public string UnifiedDisplay => Kind switch
    {
        LineDiffKind.Added => $"+{Text}",
        LineDiffKind.Removed => $"-{Text}",
        _ => $" {Text}",
    };

}

public sealed record SideBySideDiffRow(
    LineDiffKind Kind,
    string? LeftText,
    string? RightText,
    int? LeftLineNumber,
    int? RightLineNumber);

/// <summary>Local LCS line diff — no NuGet dependency.</summary>
public static class LineDiff
{

    public static IReadOnlyList<DiffLineItem> Compare(string? left, string? right)
    {

        string[] leftLines = SplitLines(left);

        string[] rightLines = SplitLines(right);

        if (leftLines.Length == 0 && rightLines.Length == 0)
        {

            return [];

        }

        int[,] lengths = BuildLcsTable(leftLines, rightLines);

        List<DiffLineItem> result = [];

        int i = leftLines.Length;

        int j = rightLines.Length;

        Stack<DiffLineItem> stack = new();

        while (i > 0 || j > 0)
        {

            if (i > 0 && j > 0 && leftLines[i - 1] == rightLines[j - 1])
            {

                stack.Push(new DiffLineItem(LineDiffKind.Unchanged, leftLines[i - 1], i, j));

                i--;

                j--;

            }
            else if (j > 0 && (i == 0 || lengths[i, j - 1] >= lengths[i - 1, j]))
            {

                stack.Push(new DiffLineItem(LineDiffKind.Added, rightLines[j - 1], null, j));

                j--;

            }
            else
            {

                stack.Push(new DiffLineItem(LineDiffKind.Removed, leftLines[i - 1], i, null));

                i--;

            }

        }

        while (stack.Count > 0)
        {

            result.Add(stack.Pop());

        }

        return result;

    }

    public static IReadOnlyList<string> BuildUnified(IReadOnlyList<DiffLineItem> lines)
    {

        List<string> unified = new(lines.Count);

        foreach (DiffLineItem line in lines)
        {

            char prefix = line.Kind switch
            {
                LineDiffKind.Added => '+',
                LineDiffKind.Removed => '-',
                _ => ' ',
            };

            unified.Add($"{prefix}{line.Text}");

        }

        return unified;

    }

    public static IReadOnlyList<SideBySideDiffRow> BuildSideBySide(IReadOnlyList<DiffLineItem> lines)
    {

        List<SideBySideDiffRow> rows = new(lines.Count);

        foreach (DiffLineItem line in lines)
        {

            rows.Add(line.Kind switch
            {
                LineDiffKind.Added => new SideBySideDiffRow(
                    LineDiffKind.Added,
                    null,
                    line.Text,
                    null,
                    line.RightLineNumber),
                LineDiffKind.Removed => new SideBySideDiffRow(
                    LineDiffKind.Removed,
                    line.Text,
                    null,
                    line.LeftLineNumber,
                    null),
                _ => new SideBySideDiffRow(
                    LineDiffKind.Unchanged,
                    line.Text,
                    line.Text,
                    line.LeftLineNumber,
                    line.RightLineNumber),
            });

        }

        return rows;

    }

    private static string[] SplitLines(string? text)
    {

        if (string.IsNullOrEmpty(text))
        {

            return [];

        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    }

    private static int[,] BuildLcsTable(string[] left, string[] right)
    {

        int[,] lengths = new int[left.Length + 1, right.Length + 1];

        for (int i = 1; i <= left.Length; i++)
        {

            for (int j = 1; j <= right.Length; j++)
            {

                if (left[i - 1] == right[j - 1])
                {

                    lengths[i, j] = lengths[i - 1, j - 1] + 1;

                }
                else
                {

                    lengths[i, j] = Math.Max(lengths[i - 1, j], lengths[i, j - 1]);

                }

            }

        }

        return lengths;

    }

}
