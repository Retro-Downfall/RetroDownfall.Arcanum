using System.Text;

namespace RetroDownfall.TheForge.Ux.Markdown;

public sealed class MarkdownImageResolver : IMarkdownImageResolver
{

    public const int MaxDataUriBytes = 512 * 1024;

    private readonly IRemoteMarkdownImageLoader _remote;

    private readonly MarkdownImageCache _cache;

    public MarkdownImageResolver(IRemoteMarkdownImageLoader remote, MarkdownImageCache? cache = null)
    {

        _remote = remote;

        _cache = cache ?? new MarkdownImageCache();

    }

    public MarkdownImageReference Classify(string? url)
    {

        if (string.IsNullOrWhiteSpace(url))
        {

            return new MarkdownImageReference(null, string.Empty, MarkdownImageKind.Disallowed);

        }

        string raw = url.Trim();

        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {

            return new MarkdownImageReference(null, raw, MarkdownImageKind.DataUri);

        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out Uri? absolute))
        {

            if (absolute.Scheme is "http" or "https")
            {

                return new MarkdownImageReference(null, raw, MarkdownImageKind.RemoteHttp);

            }

            return new MarkdownImageReference(null, raw, MarkdownImageKind.Disallowed);

        }

        return new MarkdownImageReference(null, raw, MarkdownImageKind.Relative);

    }

    public async Task<MarkdownImageResolveResult> ResolveAsync(
        MarkdownImageReference reference,
        IlluminationImageContext context,
        CancellationToken cancellationToken)
    {

        MarkdownImageReference classified = Classify(reference.RawUrl) with
        {

            AltText = reference.AltText,

        };

        return classified.Kind switch
        {
            MarkdownImageKind.RemoteHttp => await ResolveRemoteAsync(classified, context, cancellationToken)
                .ConfigureAwait(false),
            MarkdownImageKind.DataUri => ResolveDataUri(classified),
            MarkdownImageKind.Relative => Placeholder(
                "Relative image unavailable (workspace file API is text-only)."),
            _ => Placeholder("Image scheme is not allowed."),
        };

    }

    public static string NormalizeRelativePath(string? baseDirectory, string relativeUrl, out bool traversal)
    {

        traversal = false;

        List<string> baseParts = SplitPath(baseDirectory);

        List<string> stack = [.. baseParts];

        int minDepth = baseParts.Count;

        foreach (string part in SplitPath(relativeUrl))
        {

            if (part == ".")
            {

                continue;

            }

            if (part == "..")
            {

                if (stack.Count <= minDepth)
                {

                    traversal = true;

                    return string.Empty;

                }

                stack.RemoveAt(stack.Count - 1);

                continue;

            }

            stack.Add(part);

        }

        return string.Join('/', stack);

    }

    private static List<string> SplitPath(string? path)
    {

        if (string.IsNullOrWhiteSpace(path))
        {

            return [];

        }

        return path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(static part => part is not ("." or ""))
            .ToList();

    }

    private async Task<MarkdownImageResolveResult> ResolveRemoteAsync(
        MarkdownImageReference reference,
        IlluminationImageContext context,
        CancellationToken cancellationToken)
    {

        if (!context.LoadRemoteImages)
        {

            return Placeholder("Remote images are disabled.");

        }

        if (!Uri.TryCreate(reference.RawUrl, UriKind.Absolute, out Uri? uri))
        {

            return Placeholder("Invalid remote image URL.");

        }

        if (_cache.TryGet(uri.AbsoluteUri, out MarkdownImageResolveResult? cached))
        {

            return cached;

        }

        MarkdownImageResolveResult result = await _remote.LoadAsync(uri, cancellationToken).ConfigureAwait(false);

        if (result.Status == MarkdownImageResolveStatus.Success)
        {

            _cache.Put(uri.AbsoluteUri, result);

        }

        return result;

    }

    private static MarkdownImageResolveResult ResolveDataUri(MarkdownImageReference reference)
    {

        string raw = reference.RawUrl;

        if (!raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {

            return Placeholder("Invalid data URI.");

        }

        int comma = raw.IndexOf(',');

        if (comma <= 5)
        {

            return Placeholder("Invalid data URI.");

        }

        string meta = raw[5..comma];

        string payload = raw[(comma + 1)..];

        string[] metaParts = meta.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string mime = metaParts.Length > 0 ? metaParts[0].ToLowerInvariant() : string.Empty;

        bool isBase64 = metaParts.Any(static p => p.Equals("base64", StringComparison.OrdinalIgnoreCase));

        if (mime is not ("image/png" or "image/jpeg" or "image/jpg" or "image/gif" or "image/webp"))
        {

            return Placeholder("Data URI MIME type is not allowed.");

        }

        try
        {

            byte[] bytes = isBase64
                ? Convert.FromBase64String(payload)
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));

            if (bytes.Length > MaxDataUriBytes)
            {

                return Placeholder("Data URI exceeds size limit.");

            }

            if (!RemoteMarkdownImageLoader.TryValidateRaster(bytes, out string? error))
            {

                return Placeholder(error ?? "Data URI decode failed.");

            }

            return new MarkdownImageResolveResult(MarkdownImageResolveStatus.Success, bytes, mime, string.Empty);

        }
        catch (Exception ex)
        {

            return Placeholder("Data URI decode failed: " + ex.Message);

        }

    }

    private static MarkdownImageResolveResult Placeholder(string reason) =>
        new(MarkdownImageResolveStatus.Placeholder, null, null, reason);

}

public sealed class MarkdownImageCache
{

    public const int MaxEntries = 32;

    private readonly object _gate = new();

    private readonly Dictionary<string, MarkdownImageResolveResult> _map = new(StringComparer.Ordinal);

    private readonly LinkedList<string> _order = new();

    public bool TryGet(string key, out MarkdownImageResolveResult result)
    {

        lock (_gate)
        {

            if (_map.TryGetValue(key, out MarkdownImageResolveResult? found))
            {

                result = found;

                return true;

            }

        }

        result = null!;

        return false;

    }

    public void Put(string key, MarkdownImageResolveResult result)
    {

        lock (_gate)
        {

            if (_map.ContainsKey(key))
            {

                _order.Remove(key);

            }

            _map[key] = result;

            _order.AddLast(key);

            while (_order.Count > MaxEntries)
            {

                string? oldest = _order.First?.Value;

                if (oldest is null)
                {

                    break;

                }

                _order.RemoveFirst();

                _map.Remove(oldest);

            }

        }

    }

}
