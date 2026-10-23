using System.Text;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Writes a small, valid PDF at test time from numbered objects, with a correct cross-reference table, for the cases whose
/// bytes depend on the run: a port a listener owns, a path a sentinel would appear at, a text of a chosen length. The
/// committed fixtures cover everything else.
/// </summary>
internal sealed class PdfDocumentBuilder
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private readonly SortedDictionary<int, byte[]> _objects = [];

    /// <summary>Adds object <paramref name="number"/> with <paramref name="body"/> (Latin-1) as its entire body.</summary>
    public PdfDocumentBuilder Object(int number, string body)
    {
        _objects[number] = Latin1.GetBytes(body);

        return this;
    }

    /// <summary>Adds a stream object whose dictionary holds <paramref name="entries"/> next to its computed <c>/Length</c>.</summary>
    public PdfDocumentBuilder Stream(int number, string entries, ReadOnlySpan<byte> data)
    {
        byte[] head = Latin1.GetBytes($"<< /Length {data.Length} {entries} >>\nstream\n");

        byte[] tail = Latin1.GetBytes("\nendstream");

        byte[] body = new byte[head.Length + data.Length + tail.Length];

        head.CopyTo(body, 0);

        data.CopyTo(body.AsSpan(head.Length));

        tail.CopyTo(body, head.Length + data.Length);

        _objects[number] = body;

        return this;
    }

    /// <inheritdoc cref="Stream(int, string, ReadOnlySpan{byte})"/>
    public PdfDocumentBuilder Stream(int number, string entries, string data) => Stream(number, entries, Latin1.GetBytes(data));

    /// <summary>The document, with <paramref name="root"/> as the catalog.</summary>
    public byte[] Build(int root = 1)
    {
        using MemoryStream output = new();

        void Write(string text) => output.Write(Latin1.GetBytes(text));

        Write("%PDF-1.4\n%âãÏÓ\n");

        Dictionary<int, long> offsets = [];

        foreach ((int number, byte[] body) in _objects)
        {
            offsets[number] = output.Position;

            Write($"{number} 0 obj\n");

            output.Write(body);

            Write("\nendobj\n");
        }

        long xref = output.Position;

        int size = _objects.Keys.Max() + 1;

        Write($"xref\n0 {size}\n0000000000 65535 f \n");

        for (int number = 1; number < size; number++)
        {
            Write(offsets.TryGetValue(number, out long offset) ? $"{offset:D10} 00000 n \n" : "0000000000 65535 f \n");
        }

        Write($"trailer\n<< /Size {size} /Root {root} 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return output.ToArray();
    }
}
