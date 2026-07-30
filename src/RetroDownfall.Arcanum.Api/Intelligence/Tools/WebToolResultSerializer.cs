using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.Tools;

/// <summary>
/// Serializes native web-tool envelopes as valid JSON whose complete UTF-8 representation fits
/// beneath the generic tool materializer's 2,048-token / 8,192-byte default ceiling.
/// </summary>
internal static class WebToolResultSerializer
{
    internal const int MaxUtf8Bytes = 8_192;

    private const int MaxCodeBytes = 128;

    private const int MaxMessageBytes = 768;

    private const int MaxProviderBytes = 64;

    private const int MaxModelBytes = 128;

    private const int MaxTitleBytes = 512;

    private const int MaxDateBytes = 128;

    private const int MaxUrlBytes = 2_048;

    private const int MaxLinkTextBytes = 512;

    private const string TruncationMarker = "\n\n...(truncated)";

    public static string Serialize(BrowseWebResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        string[] links = (result.Links ?? [])
            .Select(static link => NormalizeAndBound(link, MaxUrlBytes))
            .ToArray();
        BrowseWebResult normalized = result with
        {
            Title = NormalizeAndBound(result.Title, MaxTitleBytes),
            Content = Normalize(result.Content),
            Links = links,
        };
        string serialized = Serialize(
            normalized,
            ArcanumJsonContext.Default.BrowseWebResult);

        if (Fits(serialized))
        {
            return serialized;
        }

        int retainedLinkCount = FindLargestRetainedCount(
            links.Length,
            minimumCount: 0,
            count => normalized with { Links = links[..count] },
            ArcanumJsonContext.Default.BrowseWebResult,
            out string? bestFit);

        if (retainedLinkCount >= 0 && bestFit is not null)
        {
            return bestFit;
        }

        BrowseWebResult withoutLinks = normalized with { Links = [] };

        return SerializeWithBoundedText(
            withoutLinks,
            normalized.Content,
            static (candidate, content) => candidate with
            {
                Content = content,
            },
            ArcanumJsonContext.Default.BrowseWebResult);
    }

    public static string Serialize(WebSearchToolResultEnvelope result)
    {
        ArgumentNullException.ThrowIfNull(result);

        WebSearchToolResultEnvelope normalized = Normalize(result);

        string serialized = Serialize(
            normalized,
            WebToolJsonContext.Default.WebSearchToolResultEnvelope);

        if (Fits(serialized))
        {
            return serialized;
        }

        WebSearchToolData? data = normalized.Data;

        if (data is null)
        {
            return SerializeSearchFallback(normalized);
        }

        int minimumCitationCount = data.Citations.Length > 0 ? 1 : 0;
        int retainedCitationCount = FindLargestRetainedCount(
            data.Citations.Length,
            minimumCitationCount,
            count => normalized with
            {
                Data = RetainCitations(data, count),
                Truncated = true,
            },
            WebToolJsonContext.Default.WebSearchToolResultEnvelope,
            out string? bestFit);

        if (retainedCitationCount >= 0 && bestFit is not null)
        {
            return bestFit;
        }

        WebSearchToolResultEnvelope boundedTemplate = normalized with
        {
            Data = RetainCitations(data, minimumCitationCount),
            Truncated = true,
        };

        if (!Fits(
                Serialize(
                    boundedTemplate with
                    {
                        Data = boundedTemplate.Data! with
                        {
                            Answer = string.Empty,
                        },
                    },
                    WebToolJsonContext.Default.WebSearchToolResultEnvelope)))
        {
            // A malicious/provider-neutral item can JSON-expand far beyond its
            // raw UTF-8 length (for example, many control characters). Prefer
            // useful answer text over collapsing to the minimal status envelope.
            boundedTemplate = normalized with
            {
                Data = RetainCitations(data, 0),
                Truncated = true,
            };
        }

        return SerializeWithBoundedText(
            boundedTemplate,
            data.Answer,
            (candidate, answer) => candidate with
            {
                Data = candidate.Data! with { Answer = answer },
                Truncated = true,
            },
            WebToolJsonContext.Default.WebSearchToolResultEnvelope);
    }

    public static string Serialize(ReadUrlToolResultEnvelope result)
    {
        ArgumentNullException.ThrowIfNull(result);

        ReadUrlToolResultEnvelope normalized = Normalize(result);

        string serialized = Serialize(
            normalized,
            WebToolJsonContext.Default.ReadUrlToolResultEnvelope);

        if (Fits(serialized))
        {
            return serialized;
        }

        ReadUrlToolData? data = normalized.Data;

        if (data is null)
        {
            return SerializeReadFallback(normalized);
        }

        int minimumLinkCount = data.Links.Length > 0 ? 1 : 0;
        int retainedLinkCount = FindLargestRetainedCount(
            data.Links.Length,
            minimumLinkCount,
            count => normalized with
            {
                Data = RetainLinks(data, count),
                Truncated = true,
            },
            WebToolJsonContext.Default.ReadUrlToolResultEnvelope,
            out string? bestFit);

        if (retainedLinkCount >= 0 && bestFit is not null)
        {
            return bestFit;
        }

        ReadUrlToolResultEnvelope boundedTemplate = normalized with
        {
            Data = RetainLinks(data, minimumLinkCount),
            Truncated = true,
        };

        if (!Fits(
                Serialize(
                    boundedTemplate with
                    {
                        Data = boundedTemplate.Data! with
                        {
                            Markdown = string.Empty,
                        },
                    },
                    WebToolJsonContext.Default.ReadUrlToolResultEnvelope)))
        {
            boundedTemplate = normalized with
            {
                Data = RetainLinks(data, 0),
                Truncated = true,
            };
        }

        return SerializeWithBoundedText(
            boundedTemplate,
            data.Markdown,
            (candidate, markdown) => candidate with
            {
                Data = candidate.Data! with { Markdown = markdown },
                Truncated = true,
            },
            WebToolJsonContext.Default.ReadUrlToolResultEnvelope);
    }

    private static WebSearchToolResultEnvelope Normalize(WebSearchToolResultEnvelope result)
    {
        WebSearchToolData? data = result.Data;

        if (data is not null)
        {
            WebToolCitation[] citations = new WebToolCitation[data.Citations.Length];
            int omittedCitationCount = Math.Max(
                0,
                data.OmittedCitationCount);

            for (int i = 0; i < citations.Length; i++)
            {
                WebToolCitation citation = data.Citations[i] ?? new WebToolCitation();

                citations[i] = citation with
                {
                    Url = NormalizeAndBound(citation.Url, MaxUrlBytes),
                    Title = NormalizeAndBoundNullable(citation.Title, MaxTitleBytes),
                    PublishedDate = NormalizeAndBoundNullable(citation.PublishedDate, MaxDateBytes),
                };
            }

            data = data with
            {
                Answer = Normalize(data.Answer),
                Citations = citations,
                TotalCitationCount = Math.Max(
                    0,
                    Math.Max(
                        data.TotalCitationCount,
                        SaturatingAdd(
                            citations.Length,
                            omittedCitationCount))),
                OmittedCitationCount = omittedCitationCount,
            };
        }

        return result with
        {
            Status = NormalizeAndBound(result.Status, MaxCodeBytes),
            Code = NormalizeAndBoundNullable(result.Code, MaxCodeBytes),
            Message = NormalizeAndBoundNullable(result.Message, MaxMessageBytes),
            Provider = NormalizeAndBound(result.Provider, MaxProviderBytes),
            Model = NormalizeAndBoundNullable(result.Model, MaxModelBytes),
            SuggestedTool = NormalizeAndBoundNullable(result.SuggestedTool, MaxCodeBytes),
            Data = data,
        };
    }

    private static ReadUrlToolResultEnvelope Normalize(ReadUrlToolResultEnvelope result)
    {
        ReadUrlToolData? data = result.Data;

        if (data is not null)
        {
            WebToolLink[] links = new WebToolLink[data.Links.Length];
            int omittedLinkCount = Math.Max(
                0,
                data.OmittedLinkCount);

            for (int i = 0; i < links.Length; i++)
            {
                WebToolLink link = data.Links[i] ?? new WebToolLink();

                links[i] = link with
                {
                    Text = NormalizeAndBound(link.Text, MaxLinkTextBytes),
                    Url = NormalizeAndBound(link.Url, MaxUrlBytes),
                };
            }

            data = data with
            {
                Title = NormalizeAndBoundNullable(data.Title, MaxTitleBytes),
                Markdown = Normalize(data.Markdown),
                FinalUrl = NormalizeAndBound(data.FinalUrl, MaxUrlBytes),
                Links = links,
                TotalLinkCount = Math.Max(
                    0,
                    Math.Max(
                        data.TotalLinkCount,
                        SaturatingAdd(
                            links.Length,
                            omittedLinkCount))),
                OmittedLinkCount = omittedLinkCount,
            };
        }

        return result with
        {
            Status = NormalizeAndBound(result.Status, MaxCodeBytes),
            Code = NormalizeAndBoundNullable(result.Code, MaxCodeBytes),
            Message = NormalizeAndBoundNullable(result.Message, MaxMessageBytes),
            Provider = NormalizeAndBound(result.Provider, MaxProviderBytes),
            SuggestedTool = NormalizeAndBoundNullable(result.SuggestedTool, MaxCodeBytes),
            Data = data,
        };
    }

    private static WebSearchToolData RetainCitations(
        WebSearchToolData data,
        int retainedCount)
    {
        retainedCount = Math.Clamp(retainedCount, 0, data.Citations.Length);
        int newlyOmitted = data.Citations.Length - retainedCount;

        return data with
        {
            Citations = data.Citations[..retainedCount],
            OmittedCitationCount = SaturatingAdd(
                data.OmittedCitationCount,
                newlyOmitted),
        };
    }

    private static ReadUrlToolData RetainLinks(
        ReadUrlToolData data,
        int retainedCount)
    {
        retainedCount = Math.Clamp(retainedCount, 0, data.Links.Length);
        int newlyOmitted = data.Links.Length - retainedCount;

        return data with
        {
            Links = data.Links[..retainedCount],
            OmittedLinkCount = SaturatingAdd(
                data.OmittedLinkCount,
                newlyOmitted),
        };
    }

    private static int FindLargestRetainedCount<T>(
        int itemCount,
        int minimumCount,
        Func<int, T> candidateFactory,
        JsonTypeInfo<T> jsonTypeInfo,
        out string? bestFit)
    {
        int low = Math.Clamp(minimumCount, 0, itemCount);
        int high = itemCount;
        int bestCount = -1;
        bestFit = null;

        while (low <= high)
        {
            int count = low + ((high - low) / 2);
            string serialized = Serialize(candidateFactory(count), jsonTypeInfo);

            if (Fits(serialized))
            {
                bestCount = count;
                bestFit = serialized;
                low = count + 1;
            }
            else
            {
                high = count - 1;
            }
        }

        return bestCount;
    }

    private static string SerializeWithBoundedText<T>(
        T template,
        string text,
        Func<T, string, T> withText,
        JsonTypeInfo<T> jsonTypeInfo)
    {
        string normalizedText = Normalize(text);
        int low = 0;
        int high = normalizedText.Length;
        string? bestFit = null;

        while (low <= high)
        {
            int requestedLength = low + ((high - low) / 2);
            int safeLength = Utf8Truncation.SafeCharSliceLength(
                normalizedText,
                requestedLength);
            string prefix = normalizedText[..safeLength];

            if (safeLength < normalizedText.Length)
            {
                prefix += TruncationMarker;
            }

            string serialized = Serialize(
                withText(template, prefix),
                jsonTypeInfo);

            if (Fits(serialized))
            {
                bestFit = serialized;
                low = requestedLength + 1;
            }
            else
            {
                high = requestedLength - 1;
            }
        }

        if (bestFit is not null)
        {
            return bestFit;
        }

        string empty = Serialize(
            withText(template, string.Empty),
            jsonTypeInfo);

        if (Fits(empty))
        {
            return empty;
        }

        const string minimal = "{\"status\":\"truncated\",\"truncated\":true}";

        return minimal;
    }

    private static string SerializeSearchFallback(
        WebSearchToolResultEnvelope result)
    {
        WebSearchToolResultEnvelope bounded = result with
        {
            Status = "error",
            Message = NormalizeAndBound(result.Message, 256),
            Model = null,
            SuggestedTool = null,
            Truncated = true,
        };

        string serialized = Serialize(
            bounded,
            WebToolJsonContext.Default.WebSearchToolResultEnvelope);

        return Fits(serialized)
            ? serialized
            : "{\"status\":\"error\",\"truncated\":true}";
    }

    private static string SerializeReadFallback(
        ReadUrlToolResultEnvelope result)
    {
        ReadUrlToolResultEnvelope bounded = result with
        {
            Status = "error",
            Message = NormalizeAndBound(result.Message, 256),
            SuggestedTool = null,
            Truncated = true,
        };

        string serialized = Serialize(
            bounded,
            WebToolJsonContext.Default.ReadUrlToolResultEnvelope);

        return Fits(serialized)
            ? serialized
            : "{\"status\":\"error\",\"truncated\":true}";
    }

    private static string Serialize<T>(T value, JsonTypeInfo<T> jsonTypeInfo) =>
        JsonSerializer.Serialize(value, jsonTypeInfo);

    private static bool Fits(string serialized) =>
        serialized.Length <= MaxUtf8Bytes
        && Encoding.UTF8.GetByteCount(serialized) <= MaxUtf8Bytes;

    private static string Normalize(string? value) =>
        Utf8Truncation.NormalizeInvalidUtf16(value ?? string.Empty);

    private static string NormalizeAndBound(string? value, int maxUtf8Bytes) =>
        Utf8Truncation.TruncateToUtf8ByteBudget(
            Normalize(value),
            maxUtf8Bytes);

    private static string? NormalizeAndBoundNullable(
        string? value,
        int maxUtf8Bytes) =>
        value is null
            ? null
            : NormalizeAndBound(value, maxUtf8Bytes);

    private static int SaturatingAdd(int left, int right) =>
        left > int.MaxValue - right
            ? int.MaxValue
            : left + right;
}
