using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.LlamaCpp;

[ExcludeFromCodeCoverage] // Reason: downloads and caches GGUF model artifacts over HTTP; same family as excluded LlamaServerManager.
public sealed class TheReliquary : IReliquary
{

    public const string HttpClientName = "LlamaModelDownload";

    private const string ModelFileName = "model.gguf";

    private const string ManifestFileName = "manifest.json";

    private const string TempDownloadSuffix = ".download.tmp";

    private static readonly TimeSpan StaleTempDownloadMaxAge = TimeSpan.FromHours(24);

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly IOptionsMonitor<ArcanumSettings> _optionsMonitor;

    private readonly IServiceProvider _serviceProvider;

    private readonly ILogger<TheReliquary> _logger;

    private readonly SemaphoreSlim _evictLock = new(1, 1);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _downloadLocks = new(StringComparer.OrdinalIgnoreCase);

    public TheReliquary(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<ArcanumSettings> optionsMonitor,
        IServiceProvider serviceProvider,
        ILogger<TheReliquary> logger)
    {

        _httpClientFactory = httpClientFactory;

        _optionsMonitor = optionsMonitor;

        _serviceProvider = serviceProvider;

        _logger = logger;

    }

    public bool IsCached(string cacheKey)
    {

        string modelPath = GetEntryModelPath(cacheKey);

        return File.Exists(modelPath);

    }

    public string? GetModelPath(string cacheKey)
    {

        string modelPath = GetEntryModelPath(cacheKey);

        return File.Exists(modelPath) ? modelPath : null;

    }

    public async Task<Result<string>> EnsureModelAsync(
        string cacheKey,
        string sourceUrl,
        string? expectedSha256,
        IProgress<LlamaPullProgress>? progress,
        CancellationToken cancellationToken)
    {

        if (!LlamaSourceUrl.TryValidate(sourceUrl, out string normalizedUrl))
        {
            return Result<string>.Failure(new Error("Llama.InvalidSourceUrl", "Source URL must be an absolute http or https URI."));
        }

        Result outbound = await OutboundUrlGuard.ValidateUntrustedUrlAsync(normalizedUrl, cancellationToken).ConfigureAwait(false);

        if (outbound.IsFailure)
        {

            return Result<string>.Failure(new Error(OutboundUrlGuard.BlockedErrorCode, outbound.Error.Message));

        }

        LlamaCppSettings llamaSettings = _optionsMonitor.CurrentValue.LlamaCpp ?? new LlamaCppSettings();

        string? resolvedSha256 = GgufModelHashPolicy.ResolveExpectedSha256(cacheKey, expectedSha256, llamaSettings);

        if (GgufModelHashPolicy.ShouldRejectUnverified(resolvedSha256, llamaSettings.RequireModelHash))
        {

            return Result<string>.Failure(new Error(
                GgufModelHashPolicy.UnverifiedDownloadCode,
                GgufModelHashPolicy.UnverifiedDownloadMessage));

        }

        bool verifiedDownload = GgufModelHashPolicy.IsVerifiedDownload(resolvedSha256);

        SweepStaleTempDownloads();

        await GetDownloadLock(cacheKey).WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string? existing = GetModelPath(cacheKey);

            if (existing is not null)
            {
                Result verifyResult = await VerifyCachedModelIntegrityAsync(
                    cacheKey,
                    existing,
                    resolvedSha256,
                    cancellationToken).ConfigureAwait(false);

                if (verifyResult.IsFailure)
                {
                    _logger.LogWarning(
                        "Cached model for {CacheKey} failed integrity verification: {Message}. Re-downloading.",
                        cacheKey,
                        verifyResult.Error.Message);

                    TryDeleteCachedEntry(cacheKey);

                    existing = null;
                }
                else
                {
                    await TouchLastAccessedAsync(cacheKey, cancellationToken).ConfigureAwait(false);

                    ReportProgress(progress, cacheKey, completed: true, bytesDownloaded: 0, totalBytes: null);

                    return Result<string>.Success(existing);
                }
            }

            string entryDir = GetEntryDirectory(cacheKey);

            Directory.CreateDirectory(entryDir);

            if (!verifiedDownload)
            {

                ReportUnverifiedDownloadWarning(progress, cacheKey);

            }

            string modelPath = Path.Combine(entryDir, ModelFileName);

            string tempPath = modelPath + TempDownloadSuffix;

            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

            Result<string> downloadResult = await DownloadWithResumeAsync(
                client,
                normalizedUrl,
                cacheKey,
                modelPath,
                tempPath,
                resolvedSha256,
                verifiedDownload,
                progress,
                redirectHop: 0,
                cancellationToken).ConfigureAwait(false);

            if (downloadResult.IsFailure)
            {
                return downloadResult;
            }

            await EvictIfNeededAsync(cancellationToken).ConfigureAwait(false);

            long finalSize = new FileInfo(modelPath).Length;

            ReportProgress(progress, cacheKey, completed: true, bytesDownloaded: finalSize, totalBytes: finalSize);

            return Result<string>.Success(modelPath);
        }
        finally
        {
            GetDownloadLock(cacheKey).Release();
        }

    }

    private SemaphoreSlim GetDownloadLock(string cacheKey) =>
        _downloadLocks.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));

    private async Task<Result<string>> DownloadWithResumeAsync(
        HttpClient client,
        string normalizedUrl,
        string cacheKey,
        string modelPath,
        string tempPath,
        string? resolvedSha256,
        bool verifiedDownload,
        IProgress<LlamaPullProgress>? progress,
        int redirectHop,
        CancellationToken cancellationToken)
    {

        long resumeOffset = 0;

        if (redirectHop == 0 && File.Exists(tempPath))
        {
            resumeOffset = new FileInfo(tempPath).Length;
        }

        using HttpRequestMessage request = new(HttpMethod.Get, normalizedUrl);

        if (resumeOffset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeOffset, null);
        }

        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (OutboundUrlGuard.IsRedirectStatusCode(response.StatusCode))
        {

            if (redirectHop >= OutboundUrlGuard.MaxUntrustedRedirectHops)
            {
                return Result<string>.Failure(new Error(
                    "Llama.DownloadFailed",
                    "Download exceeded the maximum redirect hop count."));
            }

            Result<string> nextUrl = OutboundUrlGuard.ResolveRedirectLocation(
                new Uri(normalizedUrl),
                response.Headers.Location?.ToString());

            if (nextUrl.IsFailure)
            {
                return Result<string>.Failure(nextUrl.Error);
            }

            Result redirectValidation = await OutboundUrlGuard
                .ValidateUntrustedUrlAsync(nextUrl.Value, cancellationToken)
                .ConfigureAwait(false);

            if (redirectValidation.IsFailure)
            {
                return Result<string>.Failure(new Error(
                    OutboundUrlGuard.BlockedErrorCode,
                    redirectValidation.Error.Message));
            }

            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            return await DownloadWithResumeAsync(
                client,
                nextUrl.Value,
                cacheKey,
                modelPath,
                tempPath,
                resolvedSha256,
                verifiedDownload,
                progress,
                redirectHop + 1,
                cancellationToken).ConfigureAwait(false);

        }

        if (resumeOffset > 0 && response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(tempPath);

            return await DownloadWithResumeAsync(
                client,
                normalizedUrl,
                cacheKey,
                modelPath,
                tempPath,
                resolvedSha256,
                verifiedDownload,
                progress,
                redirectHop,
                cancellationToken).ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
        {
            return Result<string>.Failure(new Error(
                "Llama.DownloadFailed",
                $"Download failed with HTTP {(int)response.StatusCode}."));
        }

        if (resumeOffset > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            File.Delete(tempPath);

            resumeOffset = 0;
        }

        Result downloadBody = await DownloadToTempAsync(
            response,
            tempPath,
            cacheKey,
            resumeOffset,
            GetModelDownloadMaxBytes(),
            progress,
            cancellationToken).ConfigureAwait(false);

        if (downloadBody.IsFailure)
        {
            return Result<string>.Failure(downloadBody.Error);
        }

        string? etag = response.Headers.ETag?.Tag;

        long? contentLength = response.Content.Headers.ContentLength;

        if (resumeOffset > 0 && contentLength.HasValue)
        {
            contentLength = resumeOffset + contentLength.Value;
        }
        else if (!contentLength.HasValue && File.Exists(tempPath))
        {
            contentLength = new FileInfo(tempPath).Length;
        }

        Result finalize = await FinalizeDownloadAsync(
            cacheKey,
            normalizedUrl,
            tempPath,
            modelPath,
            resolvedSha256,
            verifiedDownload,
            etag,
            contentLength,
            cancellationToken).ConfigureAwait(false);

        if (finalize.IsFailure)
        {
            return Result<string>.Failure(finalize.Error);
        }

        return Result<string>.Success(modelPath);

    }

    public Task<IReadOnlyList<CachedModelInfo>> ListAsync(CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        SweepStaleTempDownloads();

        string root = ArcanumPaths.ModelCacheDirectory;

        if (!Directory.Exists(root))
        {
            return Task.FromResult<IReadOnlyList<CachedModelInfo>>([]);
        }

        List<CachedModelInfo> results = [];

        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string cacheKey = Path.GetFileName(dir);

            string modelPath = Path.Combine(dir, ModelFileName);

            if (!File.Exists(modelPath))
            {
                continue;
            }

            GgufModelManifest? manifest = TryReadManifest(dir);

            if (manifest is null)
            {
                var info = new FileInfo(modelPath);

                results.Add(new CachedModelInfo
                {
                    CacheKey = cacheKey,
                    SourceUrl = string.Empty,
                    Size = info.Length,
                    DownloadedAt = info.CreationTimeUtc,
                    LastAccessedAt = info.LastAccessTimeUtc,
                });

                continue;
            }

            results.Add(new CachedModelInfo
            {
                CacheKey = cacheKey,
                SourceUrl = manifest.SourceUrl,
                Sha256 = manifest.Sha256,
                Size = manifest.Size,
                DownloadedAt = manifest.DownloadedAt,
                LastAccessedAt = manifest.LastAccessedAt,
            });
        }

        results.Sort(static (a, b) => b.LastAccessedAt.CompareTo(a.LastAccessedAt));

        return Task.FromResult<IReadOnlyList<CachedModelInfo>>(results);

    }

    public async Task<Result> DeleteAsync(string cacheKey, CancellationToken cancellationToken)
    {

        await _evictLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string entryDir = GetEntryDirectory(cacheKey);

            if (!Directory.Exists(entryDir))
            {
                return Result.Failure(new Error("Llama.ModelNotCached", $"No cached model found for key '{cacheKey}'."));
            }

            Directory.Delete(entryDir, recursive: true);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete cached model {CacheKey}.", cacheKey);

            return Result.Failure(new Error("Llama.CacheDeleteFailed", ex.Message));
        }
        finally
        {
            _evictLock.Release();
        }

    }

    private async Task<Result> DownloadToTempAsync(
        HttpResponseMessage response,
        string tempPath,
        string cacheKey,
        long resumeOffset,
        long maxBytes,
        IProgress<LlamaPullProgress>? progress,
        CancellationToken cancellationToken)
    {

        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        FileMode mode = resumeOffset > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent
            ? FileMode.Append
            : FileMode.Create;

        await using FileStream file = new(tempPath, mode, FileAccess.Write, FileShare.None, bufferSize: 81920, FileOptions.Asynchronous);

        byte[] buffer = new byte[81920];

        long totalBytes = response.Content.Headers.ContentLength ?? -1;

        if (resumeOffset > 0 && totalBytes >= 0)
        {
            totalBytes += resumeOffset;
        }

        if (totalBytes > maxBytes)
        {
            TryDeleteTempDownload(tempPath);

            return Result.Failure(new Error(
                "Llama.DownloadTooLarge",
                $"Download exceeds Arcanum:LlamaCpp:ModelDownloadMaxBytes ({maxBytes} bytes)."));
        }

        long downloaded = resumeOffset;

        while (true)
        {
            int read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            downloaded += read;

            if (downloaded > maxBytes)
            {
                TryDeleteTempDownload(tempPath);

                return Result.Failure(new Error(
                    "Llama.DownloadTooLarge",
                    $"Download exceeds Arcanum:LlamaCpp:ModelDownloadMaxBytes ({maxBytes} bytes)."));
            }

            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            ReportProgress(progress, cacheKey, completed: false, bytesDownloaded: downloaded, totalBytes: totalBytes >= 0 ? totalBytes : null);
        }

        await file.FlushAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();

    }

    private long GetModelDownloadMaxBytes() =>
        ArcanumSettingClamps.LlamaModelDownloadMaxBytes(
            _optionsMonitor.CurrentValue.LlamaCpp?.ModelDownloadMaxBytes ?? new LlamaCppSettings().ModelDownloadMaxBytes);

    private void SweepStaleTempDownloads()
    {

        string root = ArcanumPaths.ModelCacheDirectory;

        if (!Directory.Exists(root))
        {

            return;

        }

        DateTime cutoffUtc = DateTime.UtcNow - StaleTempDownloadMaxAge;

        foreach (string dir in Directory.EnumerateDirectories(root))
        {

            string cacheKey = Path.GetFileName(dir);

            string tempPath = Path.Combine(dir, ModelFileName + TempDownloadSuffix);

            if (!File.Exists(tempPath))
            {

                continue;

            }

            if (_downloadLocks.TryGetValue(cacheKey, out SemaphoreSlim? downloadLock) && downloadLock.CurrentCount == 0)
            {

                continue;

            }

            try
            {

                if (File.GetLastWriteTimeUtc(tempPath) < cutoffUtc)
                {

                    TryDeleteTempDownload(tempPath);

                    _logger.LogInformation("Removed stale temp download at {TempPath}.", tempPath);

                }

            }
            catch (Exception ex)
            {

                _logger.LogDebug(ex, "Failed to inspect stale temp download at {TempPath}.", tempPath);

            }

        }

    }

    private void TryDeleteTempDownload(string tempPath)
    {

        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete temp download at {TempPath}.", tempPath);
        }

    }

    private async Task<Result> FinalizeDownloadAsync(
        string cacheKey,
        string sourceUrl,
        string tempPath,
        string modelPath,
        string? resolvedSha256,
        bool verifiedDownload,
        string? etag,
        long? contentLength,
        CancellationToken cancellationToken)
    {

        if (!File.Exists(tempPath))
        {
            return Result.Failure(new Error("Llama.DownloadFailed", "Download did not produce a file."));
        }

        string? computedHash = null;

        if (!string.IsNullOrWhiteSpace(resolvedSha256))
        {

            computedHash = await ComputeSha256HexAsync(tempPath, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(computedHash, resolvedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {

                try
                {

                    File.Delete(tempPath);

                }
                catch (Exception ex)
                {

                    _logger.LogWarning(ex, "Failed to delete temp download after SHA256 mismatch for {CacheKey}.", cacheKey);

                }

                return Result.Failure(new Error("Llama.Sha256Mismatch", "Downloaded file SHA256 does not match the expected hash."));

            }

        }
        else
        {

            computedHash = await ComputeSha256HexAsync(tempPath, cancellationToken).ConfigureAwait(false);

        }

        File.Move(tempPath, modelPath, overwrite: true);

        long size = new FileInfo(modelPath).Length;

        DateTimeOffset now = DateTimeOffset.UtcNow;

        GgufModelManifest manifest = new()
        {

            SourceUrl = sourceUrl,

            Etag = etag,

            Sha256 = computedHash,

            DownloadedAt = now,

            LastAccessedAt = now,

            Size = size,

            Verified = verifiedDownload,

        };

        string manifestPath = Path.Combine(GetEntryDirectory(cacheKey), ManifestFileName);

        await WriteManifestAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);

        return Result.Success();

    }

    // W2.5 Fix 3: write the manifest atomically (same-directory temp + flush +
    // File.Move(overwrite)) reusing SpellAtomicFile (W2.1), so a crash between
    // the model File.Move and the manifest write cannot leave a model with a
    // partial/missing manifest. The model move itself is already atomic.
    internal static Task WriteManifestAtomicAsync(string manifestPath, GgufModelManifest manifest, CancellationToken cancellationToken)
    {

        string manifestJson = JsonSerializer.Serialize(manifest, LlamaCppJsonContext.Default.GgufModelManifest);

        return SpellAtomicFile.WriteAllTextAsync(manifestPath, manifestJson, cancellationToken);

    }

    private async Task EvictIfNeededAsync(CancellationToken cancellationToken)
    {

        await _evictLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            int maxCached = ArcanumSettingClamps.LlamaMaxCachedModels(
                _optionsMonitor.CurrentValue.LlamaCpp?.MaxCachedModels ?? new LlamaCppSettings().MaxCachedModels);

            string root = ArcanumPaths.ModelCacheDirectory;

        if (!Directory.Exists(root))
        {
            return;
        }

        List<(string CacheKey, DateTimeOffset LastAccessed)> entries = [];

        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string cacheKey = Path.GetFileName(dir);

            if (!File.Exists(Path.Combine(dir, ModelFileName)))
            {
                continue;
            }

            GgufModelManifest? manifest = TryReadManifest(dir);

            DateTimeOffset lastAccessed = manifest?.LastAccessedAt ?? File.GetLastAccessTimeUtc(Path.Combine(dir, ModelFileName));

            entries.Add((cacheKey, lastAccessed));
        }

        if (entries.Count <= maxCached)
        {
            return;
        }

        ILlamaServerManager? manager = _serviceProvider.GetService<ILlamaServerManager>();

        entries.Sort(static (a, b) => a.LastAccessed.CompareTo(b.LastAccessed));

        int toEvict = entries.Count - maxCached;

        for (int i = 0; i < entries.Count && toEvict > 0; i++)
        {
            (string cacheKey, _) = entries[i];

            if (manager is not null && manager.IsModelInUse(cacheKey))
            {
                continue;
            }

            try
            {
                Directory.Delete(GetEntryDirectory(cacheKey), recursive: true);

                toEvict--;

                _logger.LogInformation("Evicted cached model {CacheKey} (LRU).", cacheKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to evict cached model {CacheKey}.", cacheKey);
            }
        }

        }
        finally
        {

            _evictLock.Release();

        }

    }

    private async Task TouchLastAccessedAsync(string cacheKey, CancellationToken cancellationToken)
    {

        string entryDir = GetEntryDirectory(cacheKey);

        string manifestPath = Path.Combine(entryDir, ManifestFileName);

        if (!File.Exists(manifestPath))
        {
            return;
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);

            GgufModelManifest? manifest = JsonSerializer.Deserialize(bytes, LlamaCppJsonContext.Default.GgufModelManifest);

            if (manifest is null)
            {
                return;
            }

            manifest = manifest with { LastAccessedAt = DateTimeOffset.UtcNow };

            byte[] updated = JsonSerializer.SerializeToUtf8Bytes(manifest, LlamaCppJsonContext.Default.GgufModelManifest);

            await File.WriteAllBytesAsync(manifestPath, updated, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update last-accessed for cached model {CacheKey}.", cacheKey);
        }

    }

    private async Task<Result> VerifyCachedModelIntegrityAsync(
        string cacheKey,
        string modelPath,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {

        if (!File.Exists(modelPath))
        {
            return Result.Failure(new Error("Llama.CacheCorrupt", "Cached model file is missing."));
        }

        string computedHash = await ComputeSha256HexAsync(modelPath, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(expectedSha256)
            && !string.Equals(computedHash, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(new Error("Llama.Sha256Mismatch", "Cached model SHA256 does not match the expected hash."));
        }

        GgufModelManifest? manifest = TryReadManifest(GetEntryDirectory(cacheKey));

        if (manifest?.Sha256 is { Length: > 0 } manifestHash
            && !string.Equals(computedHash, manifestHash.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(new Error("Llama.Sha256Mismatch", "Cached model SHA256 does not match the manifest."));
        }

        return Result.Success();

    }

    private void TryDeleteCachedEntry(string cacheKey)
    {

        string entryDir = GetEntryDirectory(cacheKey);

        try
        {
            if (Directory.Exists(entryDir))
            {
                Directory.Delete(entryDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete cached model entry for {CacheKey}.", cacheKey);
        }

    }

    private static GgufModelManifest? TryReadManifest(string entryDir)
    {

        string manifestPath = Path.Combine(entryDir, ManifestFileName);

        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(manifestPath);

            return JsonSerializer.Deserialize(bytes, LlamaCppJsonContext.Default.GgufModelManifest);
        }
        catch
        {
            return null;
        }

    }

    private static async Task<string> ComputeSha256HexAsync(string filePath, CancellationToken cancellationToken)
    {

        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexString(hash).ToLowerInvariant();

    }

    private void ReportUnverifiedDownloadWarning(IProgress<LlamaPullProgress>? progress, string cacheKey)
    {

        _logger.LogWarning(
            "GGUF download for {CacheKey} proceeding without SHA-256 verification ({Setting}=false).",
            cacheKey,
            "Arcanum:LlamaCpp:RequireModelHash");

        if (progress is null)
        {

            return;

        }

        progress.Report(new LlamaPullProgress
        {

            CacheKey = cacheKey,

            Warning = GgufModelHashPolicy.UnverifiedDownloadWarning,

            Completed = false,

        });

    }

    private static void ReportProgress(
        IProgress<LlamaPullProgress>? progress,
        string cacheKey,
        bool completed,
        long bytesDownloaded,
        long? totalBytes)
    {

        if (progress is null)
        {
            return;
        }

        double? percent = null;

        if (totalBytes is > 0)
        {
            percent = Math.Round(bytesDownloaded * 100.0 / totalBytes.Value, 2);
        }

        progress.Report(new LlamaPullProgress
        {
            CacheKey = cacheKey,
            BytesDownloaded = bytesDownloaded,
            TotalBytes = totalBytes,
            Percent = percent,
            Completed = completed,
        });

    }

    private static string GetEntryDirectory(string cacheKey) =>
        Path.Combine(ArcanumPaths.ModelCacheDirectory, cacheKey);

    private static string GetEntryModelPath(string cacheKey) =>
        Path.Combine(GetEntryDirectory(cacheKey), ModelFileName);

}
