using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Binds a reusable token breakdown to the exact message/options payload it measured without
/// exposing prompt content in diagnostics.
/// </summary>
internal static class ModelCallPayloadFingerprint
{
    public static string ComputeText(string text)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, text);
        return Finish(hash);
    }

    public static string Compute(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (ChatMessage message in messages)
        {
            Append(hash, message.Role.ToString());
            Append(hash, message.AuthorName);
            Append(hash, message.MessageId);
            Append(hash, ModelTokenEstimator.FormatValue(message.AdditionalProperties));
            Append(hash, ModelTokenEstimator.FormatValue(message.RawRepresentation));

            foreach (AIContent content in message.Contents)
            {
                Append(hash, content.GetType().FullName);
                Append(hash, ModelTokenEstimator.FormatValue(content.AdditionalProperties));
                Append(hash, ModelTokenEstimator.FormatValue(content.RawRepresentation));
                switch (content)
                {
                    case TextContent text:
                        Append(hash, text.Text);
                        break;

                    case TextReasoningContent reasoning:
                        Append(hash, reasoning.Text);
                        Append(hash, reasoning.ProtectedData);
                        break;

                    case FunctionCallContent call:
                        Append(hash, call.CallId);
                        Append(hash, call.Name);
                        Append(hash, ModelTokenEstimator.FormatValue(call.Arguments));
                        break;

                    case FunctionResultContent result:
                        Append(hash, result.CallId);
                        Append(hash, ModelTokenEstimator.FormatValue(result.Result));
                        break;

                    case DataContent data:
                        Append(hash, data.MediaType);
                        Append(hash, data.Name);
                        hash.AppendData(data.Data.Span);
                        break;

                    case UriContent uri:
                        Append(hash, uri.MediaType);
                        Append(hash, uri.Uri?.ToString());
                        break;

                    default:
                        Append(hash, content.ToString());
                        break;
                }
            }
        }

        if (options.Tools is { } tools)
        {
            foreach (AITool tool in tools)
            {
                Append(hash, tool.GetType().FullName);
                Append(hash, tool.Name);
                Append(hash, tool.Description);
                Append(hash, ModelTokenEstimator.FormatValue(tool.AdditionalProperties));
                if (tool is AIFunction function)
                {
                    Append(hash, function.JsonSchema.GetRawText());
                }
            }
        }

        Append(hash, options.ToolMode?.ToString());
        Append(hash, options.Reasoning?.Effort?.ToString());
        Append(hash, options.Reasoning?.Output?.ToString());
        Append(hash, options.MaxOutputTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, ModelTokenEstimator.FormatValue(options.AdditionalProperties));

        if (options.StopSequences is { } stops)
        {
            foreach (string stop in stops)
            {
                Append(hash, stop);
            }
        }

        if (options.ResponseFormat is ChatResponseFormatJson json)
        {
            Append(hash, json.SchemaName);
            Append(hash, json.SchemaDescription);
            if (json.Schema is JsonElement schema)
            {
                Append(hash, schema.GetRawText());
            }
        }
        else
        {
            Append(hash, options.ResponseFormat?.GetType().FullName);
        }

        return Finish(hash);
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            Span<byte> missing = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(missing, -1);
            hash.AppendData(missing);
            return;
        }

        int byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, byteCount);
        hash.AppendData(length);
        if (byteCount == 0)
        {
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            int written = Encoding.UTF8.GetBytes(value, rented);
            hash.AppendData(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static string Finish(IncrementalHash hash)
    {
        Span<byte> digest = stackalloc byte[32];
        _ = hash.TryGetHashAndReset(digest, out int written);
        return Convert.ToHexString(digest[..Math.Min(written, 16)]);
    }
}
