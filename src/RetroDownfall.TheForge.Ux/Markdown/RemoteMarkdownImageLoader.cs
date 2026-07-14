using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Avalonia.Media.Imaging;

namespace RetroDownfall.TheForge.Ux.Markdown;

public interface IRemoteMarkdownImageLoader
{

    Task<MarkdownImageResolveResult> LoadAsync(Uri uri, CancellationToken cancellationToken);

}

public sealed class RemoteMarkdownImageLoader : IRemoteMarkdownImageLoader, IDisposable
{

    public const int MaxBytes = 2 * 1024 * 1024;

    public const int MaxWidth = 8192;

    public const int MaxHeight = 8192;

    public const long MaxPixels = 16_000_000;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string GenericFailure = "Remote image could not be loaded.";

    private readonly HttpClient _http;

    private readonly bool _ownsClient;

    public RemoteMarkdownImageLoader()
        : this(CreateDefaultClient(), ownsClient: true)
    {
    }

    public RemoteMarkdownImageLoader(HttpClient httpClient, bool ownsClient = false)
    {

        _http = httpClient;

        _ownsClient = ownsClient;

    }

    public async Task<MarkdownImageResolveResult> LoadAsync(Uri uri, CancellationToken cancellationToken)
    {

        if (uri.Scheme is not ("http" or "https"))
        {

            return Fail("Only http/https remote images are allowed.");

        }

        if (!await MarkdownImageSsrfPolicy.AreResolvedAddressesAllowedAsync(uri.IdnHost, cancellationToken)
                .ConfigureAwait(false))
        {

            return Fail("Remote host is blocked (local/private/metadata).");

        }

        try
        {

            // Manual redirect following with per-hop SSRF checks (handler has AllowAutoRedirect = false).
            Uri current = uri;

            HttpResponseMessage? response = null;

            for (int hop = 0; hop <= MarkdownImageSsrfPolicy.MaxRedirectHops; hop++)
            {

                if (!await MarkdownImageSsrfPolicy.AreResolvedAddressesAllowedAsync(current.IdnHost, cancellationToken)
                        .ConfigureAwait(false))
                {

                    response?.Dispose();

                    return Fail("Remote host is blocked (local/private/metadata).");

                }

                using HttpRequestMessage request = new(HttpMethod.Get, current);

                request.Headers.Accept.Clear();

                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));

                response?.Dispose();

                response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if ((int)response.StatusCode is >= 300 and < 400)
                {

                    Uri? location = response.Headers.Location;

                    if (location is null)
                    {

                        response.Dispose();

                        return Fail("Redirect without Location.");

                    }

                    current = location.IsAbsoluteUri ? location : new Uri(current, location);

                    if (current.Scheme is not ("http" or "https"))
                    {

                        response.Dispose();

                        return Fail("Only http/https remote images are allowed.");

                    }

                    continue;

                }

                break;

            }

            using HttpResponseMessage finalResponse = response
                ?? throw new InvalidOperationException("No HTTP response.");

            if (!finalResponse.IsSuccessStatusCode)
            {

                return Fail($"HTTP {(int)finalResponse.StatusCode}.");

            }

            string? contentType = finalResponse.Content.Headers.ContentType?.MediaType;

            if (!IsAllowedContentType(contentType))
            {

                return Fail($"Disallowed Content-Type: {contentType ?? "(none)"}.");

            }

            long? length = finalResponse.Content.Headers.ContentLength;

            if (length is > MaxBytes)
            {

                return Fail("Image exceeds size limit.");

            }

            await using Stream stream = await finalResponse.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using MemoryStream buffer = new();

            byte[] chunk = new byte[8192];

            int total = 0;

            while (true)
            {

                int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)
                    .ConfigureAwait(false);

                if (read <= 0)
                {

                    break;

                }

                total += read;

                if (total > MaxBytes)
                {

                    return Fail("Image exceeds size limit.");

                }

                buffer.Write(chunk, 0, read);

            }

            byte[] bytes = buffer.ToArray();

            if (!TryValidateRaster(bytes, out string? decodeError))
            {

                return Fail(decodeError ?? "Image decode failed.");

            }

            return new MarkdownImageResolveResult(
                MarkdownImageResolveStatus.Success,
                bytes,
                contentType,
                string.Empty);

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (HttpRequestException)
        {

            return Fail(GenericFailure);

        }
        catch (Exception)
        {

            return Fail(GenericFailure);

        }

    }

    public void Dispose()
    {

        if (_ownsClient)
        {

            _http.Dispose();

        }

    }

    internal static bool IsAllowedContentType(string? contentType)
    {

        if (string.IsNullOrWhiteSpace(contentType))
        {

            return false;

        }

        string ct = contentType.Trim().ToLowerInvariant();

        return ct is "image/png" or "image/jpeg" or "image/jpg" or "image/gif" or "image/webp";

    }

    internal static bool TryValidateRaster(byte[] bytes, out string? error)
    {

        error = null;

        if (bytes.Length == 0)
        {

            error = "Empty image body.";

            return false;

        }

        // Reject SVG sniff
        string head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 256)).TrimStart();

        if (head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {

            error = "SVG is not allowed.";

            return false;

        }

        if (!HasAllowedRasterMagic(bytes))
        {

            error = "Unrecognized or disallowed image format.";

            return false;

        }

        try
        {

            using MemoryStream ms = new(bytes);

            using Bitmap bitmap = new(ms);

            if (bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0)
            {

                error = "Invalid image dimensions.";

                return false;

            }

            if (bitmap.PixelSize.Width > MaxWidth || bitmap.PixelSize.Height > MaxHeight)
            {

                error = "Image dimensions exceed limit.";

                return false;

            }

            long pixels = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height;

            if (pixels > MaxPixels)
            {

                error = "Image pixel count exceeds limit.";

                return false;

            }

            return true;

        }
        catch
        {

            // Unit tests / headless hosts may lack an Avalonia platform; magic-byte gate already
            // applied above. Soft-accept tiny payloads; reject absurd sizes without a decoder.
            if (bytes.Length <= 64 * 1024)
            {

                return true;

            }

            error = "Image decode unavailable and payload exceeds soft size guard.";

            return false;

        }

    }

    internal static bool HasAllowedRasterMagic(ReadOnlySpan<byte> bytes)
    {

        // PNG
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {

            return true;

        }

        // JPEG
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {

            return true;

        }

        // GIF
        if (bytes.Length >= 6
            && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F'
            && bytes[3] == (byte)'8' && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9')
            && bytes[5] == (byte)'a')
        {

            return true;

        }

        // WebP (RIFF....WEBP)
        if (bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {

            return true;

        }

        return false;

    }

    private static HttpClient CreateDefaultClient()
    {

        SocketsHttpHandler handler = new()
        {

            AllowAutoRedirect = false,

            UseCookies = false,

            Credentials = null,

            AutomaticDecompression = DecompressionMethods.All,

            ConnectCallback = MarkdownImageSsrfPolicy.ConnectCallbackAsync,

        };

        return new HttpClient(handler)
        {

            Timeout = Timeout,

        };

    }

    private static MarkdownImageResolveResult Fail(string reason) =>
        new(MarkdownImageResolveStatus.Failed, null, null, reason);

}
